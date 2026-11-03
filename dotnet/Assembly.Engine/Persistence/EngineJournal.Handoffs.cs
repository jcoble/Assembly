using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    private sealed record HandoffSource(long Version, string State, string? Reason, string Machine,
        string SessionId, string OldHolder, string Target, long OldEpoch, long NextEpoch,
        string SessionMachine, string Holder, long Epoch, long? ReservedEpoch, string SessionState);
    private sealed record HandoffReceipt(string Fingerprint, string Json, CommandResult Result);

    internal static HandoffAck ReadHandoffAck(IReadScope scope, string machine, HandoffIdentity identity)
    {
        if (!ValidIdentity(scope, machine, identity)) return Ack(identity, HandoffGate.Refused, "bad_args");
        return machine == identity.SourceMachine ? ReadSourceAck(scope, identity) : ReadTargetAck(scope, identity);
    }

    internal static HandoffAck ExecuteHandoffPhase(IWriteScope scope, string machine, HandoffPhaseArgs args, long nowMs)
    {
        var identity = args.Identity;
        if (!ValidIdentity(scope, machine, identity) || machine != identity.SourceMachine || !Enum.IsDefined(args.Phase))
            return Ack(identity, HandoffGate.Refused, "bad_args");
        var prior = ReadSourceAck(scope, identity);
        var source = Source(scope, identity);
        if (source is null || prior.Gate is HandoffGate.Unknown or HandoffGate.Refused) return prior;
        var accept = args.Phase == HandoffPhase.Accept;
        var key = accept ? HandoffAdmissionKeys.SourceAccept(identity) : HandoffAdmissionKeys.SourceTerminal(identity);
        if (HasReceipt(scope, key)) return prior;
        if (source.State is "resumed" or "failed" || accept && source.State != "requested"
            || args.ObservedHandoffVersion != source.Version)
            return prior with { Gate = HandoffGate.Advanced };
        if (accept && args.Reason is not null) return Ack(identity, HandoffGate.Refused, "bad_args", source);
        var ready = CheckHandoffSourceReady(scope, machine, identity, accept);
        if (ready is not null) return ready;
        if (!accept && (source.State == "accepted" || args.Phase == HandoffPhase.Resumed))
        {
            if (source.State != "accepted" || !TargetAuthorizes(args))
                return Ack(identity, HandoffGate.Unknown, "target_evidence_required", source);
        }
        if (!accept && args.Phase == HandoffPhase.Failed && string.IsNullOrWhiteSpace(args.Reason))
            return Ack(identity, HandoffGate.Refused, "bad_args", source);
        var admission = HandoffAdmission.Phase(scope, identity, args.Phase, source.State == "accepted", args.Reason, nowMs);
        ExecuteCore(scope, admission.Context, admission.Key, admission.Canonical,
            () => HandoffTransitions.ApplyAdmitted(scope, admission), handoffAdmission: admission);
        return ReadSourceAck(scope, identity);
    }

    internal static HandoffAck? CheckHandoffSourceReady(IReadScope scope, string machine, HandoffIdentity identity, bool accept)
    {
        var source = Source(scope, identity);
        if (source is null || !Matches(source, identity) || source.SessionMachine != machine
            || source.Holder != "local" && source.Holder != machine || source.Epoch != identity.OldEpoch
            || source.ReservedEpoch != identity.NextEpoch || source.SessionState != "handing_off")
            return Ack(identity, HandoffGate.Unknown, "source_fence_changed", source);
        // 'local' is the physical marker used by ordinary Engine consumers. Its logical holder is THIS row.machine,
        // never an unrelated runtime or a mirror's machine. Do not migrate it to an Engine ID on restore.
        if (!accept) return null;
        if (scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')",
                row => row.Long(0), new SqlArg("$id", identity.SourceSessionId)) != 0
            || scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_session WHERE parent_id=$id AND state NOT IN ('ended','moved'))",
                row => row.Long(0), new SqlArg("$id", identity.SourceSessionId)) != 0)
            return Ack(identity, HandoffGate.Waiting, "unavailable", source);
        if (scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id AND state NOT IN ('done','failed','dead'))",
            row => row.Long(0), new SqlArg("$id", identity.SourceSessionId)) != 0)
            return Ack(identity, HandoffGate.Waiting, "unavailable", source);
        // Failed/dead/idle and an empty outbox do not establish positive adapter disposal. Native capture and
        // resumability remain separate T9/P2 prerequisites, never inferred from this public ACP identity.
        return ReadHandoffRelease(scope, machine, identity) is not null ? null
            : Ack(identity, HandoffGate.Unknown, "release_evidence_required", source);
    }

    internal static HandoffAck ExecuteHandoffAdopt(IWriteScope scope, string machine, HandoffAdoptArgs args, long nowMs)
    {
        var identity = args.Identity;
        if (!ValidIdentity(scope, machine, identity) || machine != identity.TargetMachine
            || args.Command.FromMachine != identity.SourceMachine || args.Command.FromSessionId != identity.SourceSessionId
            || args.Command.Epoch != identity.NextEpoch || string.IsNullOrWhiteSpace(args.Command.ProviderSessionId)
            || !RelayHash.IsSha(args.SourceAcceptFingerprint) || !RelayHash.IsSha(args.TransferCaptureSha256))
            return Ack(identity, HandoffGate.Refused, "bad_args");
        var prior = ReadTargetAck(scope, identity);
        if (HasReceipt(scope, HandoffAdmissionKeys.TargetAdmission(machine, identity.HandoffId)) || prior.Gate == HandoffGate.Unknown)
            return prior;
        // This door cannot turn an owner assertion/capture digest into the missing actual target native witness.
        // Preparation must come from P2/T9's exact native/account/storage producer before a binding/start is minted.
        return Ack(identity, HandoffGate.Waiting, "native_witness_required");
    }

    internal static HandoffAck ExecuteHandoffSeal(IWriteScope scope, string machine, HandoffSealArgs args, long nowMs)
    {
        var identity = args.Identity;
        if (!ValidIdentity(scope, machine, identity) || machine != identity.TargetMachine)
            return Ack(identity, HandoffGate.Refused, "bad_args");
        var prior = ReadTargetAck(scope, identity);
        if (HasReceipt(scope, HandoffAdmissionKeys.TargetAdmission(machine, identity.HandoffId)))
            return prior; // Adopt-first owns reconciliation. Missing operation/disposal facts cannot manufacture closure.
        if (prior.Gate == HandoffGate.Unknown || scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id)",
            row => row.Long(0), new SqlArg("$id", identity.HandoffId)) != 0)
            return Ack(identity, HandoffGate.Unknown, "target_ownership_unknown");
        var admission = HandoffAdmission.NoAdopt(scope, identity, nowMs);
        ExecuteCore(scope, admission.Context, admission.Key, admission.Canonical,
            () => new(false, false, "handoff_not_adopted", 0, 0, identity.NextEpoch), handoffAdmission: admission);
        return ReadTargetAck(scope, identity);
    }

    private static HandoffAck ReadSourceAck(IReadScope scope, HandoffIdentity identity)
    {
        var source = Source(scope, identity);
        if (source is null || !Matches(source, identity)) return Ack(identity, HandoffGate.Unknown, "source_identity_changed", source);
        try
        {
            var acceptKey = HandoffAdmissionKeys.SourceAccept(identity);
            var terminalKey = HandoffAdmissionKeys.SourceTerminal(identity);
            var accepted = ReadReceipt(scope, acceptKey);
            if (accepted is not null && !PhaseReceiptMatches(identity, HandoffPhase.Accept, false, null, accepted))
                return Ack(identity, HandoffGate.Unknown, "accept_receipt_invalid", source);
            var terminal = ReadReceipt(scope, terminalKey);
            if (terminal is not null)
            {
                if (source.State is not ("failed" or "resumed") || !PhaseReceiptMatches(identity,
                    source.State == "resumed" ? HandoffPhase.Resumed : HandoffPhase.Failed, accepted?.Result.Applied == true, source.Reason, terminal)
                    || terminal.Result.Applied && (source.Version != terminal.Result.Version || source.State == "resumed" && accepted?.Result.Applied != true))
                    return Ack(identity, HandoffGate.Unknown, "terminal_receipt_invalid", source);
                return Ack(identity, HandoffGate.Recorded, terminal.Result.Error, source, terminalKey, terminal);
            }
            if (accepted is not null)
            {
                if (accepted.Result.Applied && (source.State != "accepted" || source.Version != 2))
                    return Ack(identity, HandoffGate.Unknown, "accept_row_missing", source);
                return Ack(identity, HandoffGate.Recorded, accepted.Result.Error, source, acceptKey, accepted);
            }
            return source.State == "requested" && source.Version == 1
                ? Ack(identity, HandoffGate.Waiting, "unavailable", source)
                : Ack(identity, HandoffGate.Unknown, "phase_receipt_missing", source);
        }
        catch (JsonException) { return Ack(identity, HandoffGate.Unknown, "receipt_invalid", source); }
    }

    private static HandoffAck ReadTargetAck(IReadScope scope, HandoffIdentity identity)
    {
        var key = HandoffAdmissionKeys.TargetAdmission(identity.TargetMachine, identity.HandoffId);
        var present = scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_session WHERE id=$id)", row => row.Long(0), new SqlArg("$id", identity.HandoffId)) != 0;
        try
        {
            var receipt = ReadReceipt(scope, key);
            if (receipt is null)
            {
                var unknown = present || HasReceipt(scope, HandoffAdmissionKeys.TargetClosed(identity.TargetMachine, identity.HandoffId));
                return Ack(identity, unknown ? HandoffGate.Unknown : HandoffGate.Waiting, unknown ? "admission_missing" : "unavailable");
            }
            var expected = Fingerprint(HandoffAdmissionKeys.TargetContext(identity, false, 0), HandoffAdmission.NoAdoptCanonical(identity));
            if (receipt.Fingerprint == expected && receipt.Result is { Applied: false, Error: "handoff_not_adopted", Version: 0, Seq: 0 }
                && receipt.Result.Epoch == identity.NextEpoch && !present
                && !HasReceipt(scope, HandoffAdmissionKeys.TargetClosed(identity.TargetMachine, identity.HandoffId))
                && scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id)", row => row.Long(0), new SqlArg("$id", identity.HandoffId)) == 0)
                return new(identity, HandoffGate.Recorded, receipt.Result.Error, key, receipt.Fingerprint, receipt.Result, null, null,
                    new(identity, HandoffTargetState.NotAdopted, receipt.Fingerprint, null, 0, identity.NextEpoch, null, null, null));
            var fence = ReadTargetHandoffFence(scope, identity.TargetMachine, identity.HandoffId);
            if (fence.State == HandoffFenceState.Adopted && fence.Binding?.Identity == identity)
                return new(identity, HandoffGate.Recorded, "operation_evidence_required", key, receipt.Fingerprint,
                    receipt.Result, null, null, null);
            return Ack(identity, HandoffGate.Unknown, "adopted_operation_evidence_required");
        }
        catch (JsonException) { return Ack(identity, HandoffGate.Unknown, "receipt_invalid"); }
    }

    internal static HandoffReleaseEffectData? ReadHandoffRelease(IReadScope scope, string machine, HandoffIdentity identity)
    {
        if (!ValidIdentity(scope, machine, identity) || machine != identity.SourceMachine) return null;
        var source = Source(scope, identity);
        if (source is null || source.State is not ("requested" or "accepted") || !Matches(source, identity)
            || source.Holder != "local" && source.Holder != machine || source.Epoch != identity.OldEpoch
            || source.ReservedEpoch != identity.NextEpoch || source.SessionState != "handing_off") return null;
        var releases = scope.Query("""
            SELECT id,state,run_id,payload_json,attempt,receipt_json FROM engine_outbox
            WHERE machine=$machine AND session_id=$session AND kind='release'
                AND json_extract(payload_json,'$.identity.handoffId')=$handoff ORDER BY rowid LIMIT 2
            """, row => new
        {
            Id = row.Text(0),
            State = row.Text(1),
            Run = row.IsNull(2) ? null : row.Text(2),
            Payload = row.Text(3),
            Attempt = checked((int)row.Long(4)),
            Receipt = row.IsNull(5) ? null : row.Text(5),
        }, new("$machine", machine), new("$session", identity.SourceSessionId), new("$handoff", identity.HandoffId));
        if (releases.Count != 1) return null;
        var actual = releases[0];
        if (actual.State != "done" || actual.Receipt is null) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize(actual.Receipt, EngineJson.Default.EffectReceipt);
            using var payload = JsonDocument.Parse(actual.Payload);
            var effect = new Effect(actual.Id, "release", identity.SourceSessionId, actual.Run, payload.RootElement, actual.Attempt);
            var context = new CommandContext(Guid.Empty, machine, new(ActorKind.System, "engine.worker"),
                actual.Id, 0, identity.OldEpoch, 0);
            return receipt is not null && ValidHandoffReleaseConfirmation(scope, context, effect, receipt)
                ? receipt.Data.Deserialize(EngineJson.Default.HandoffReleaseEffectData) : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    internal static bool ValidHandoffReleaseConfirmation(IReadScope scope, CommandContext context, Effect effect, EffectReceipt receipt)
    {
        try
        {
            var intent = effect.Payload.Deserialize(EngineJson.Default.HandoffReleaseEffect);
            var data = receipt.Data.Deserialize(EngineJson.Default.HandoffReleaseEffectData);
            if (intent is null || data is null || receipt.Disposition != EffectDisposition.Confirmed || effect.Kind != "release"
                || data.Identity != intent.Identity || data.SessionId != intent.Identity.SourceSessionId
                || data.Epoch != intent.Identity.OldEpoch || context.Epoch != intent.Identity.OldEpoch
                || context.Machine != intent.Identity.SourceMachine || context.Actor.Kind != ActorKind.System
                || effect.SessionId != intent.Identity.SourceSessionId || data.ReleaseEffectId != effect.Id
                || data.ReleaseAttempt != effect.Attempt || effect.Attempt < 1 || string.IsNullOrWhiteSpace(data.AdapterOperationId)
                || string.IsNullOrWhiteSpace(intent.ProviderSessionId) || data.ProviderSessionId != intent.ProviderSessionId
                || !ValidIdentity(scope, context.Machine, intent.Identity)) return false;
            var source = Source(scope, intent.Identity);
            if (source is null || !Matches(source, intent.Identity)
                || source.Holder != "local" && source.Holder != context.Machine || source.Epoch != intent.Identity.OldEpoch) return false;
            var fenced = source.State is "requested" or "accepted" && source.ReservedEpoch == intent.Identity.NextEpoch
                && source.SessionState == "handing_off";
            // Expiry/failure may restore idle while this exact operation is retiring. Its real late completion
            // must drain the old ordered release, without gaining transfer authority or touching a replacement.
            var restored = source.State == "failed" && source.ReservedEpoch is null && source.SessionState == "idle";
            if (!fenced && !restored) return false;
            return scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE id=$id AND machine=$machine AND provider=$provider
                    AND account_context=$account AND provider_session_id=$acp
                """, row => row.Long(0), new("$id", intent.Identity.SourceSessionId), new("$machine", context.Machine),
                new("$provider", intent.Provider), new("$account", intent.AccountContext), new("$acp", intent.ProviderSessionId)) == 1;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private sealed record HandoffFenceRow(string? Machine, string? Provider, string? Account, string? Native,
        long? Epoch, string? Kind, string? StartSession, string? Payload, string? AdmissionFingerprint,
        string? AdmissionJson, string? ClosedFingerprint, string? ClosedJson);

    internal static HandoffTargetFence ReadTargetHandoffFence(IReadScope scope, string machine, string sessionId)
    {
        HandoffTargetFence Unknown() => new(HandoffFenceState.Unknown, null, "handoff_binding_invalid");
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(sessionId)
            || scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != machine)
            return Unknown();
        // These three exact keys are known from the normal owning worker/native inputs, before any JSON is read.
        var row = scope.QueryFirst("""
            SELECT s.machine,s.provider,s.account_context,s.provider_session_id,s.epoch,o.kind,o.session_id,o.payload_json,
                a.fingerprint,a.result_json,c.fingerprint,c.result_json
            FROM (SELECT 1) seed
            LEFT JOIN engine_session s ON s.id=$session
            LEFT JOIN engine_outbox o ON o.id=$start
            LEFT JOIN engine_command_receipt a ON a.command_id=$admission
            LEFT JOIN engine_command_receipt c ON c.command_id=$closed
            """, value => new HandoffFenceRow(value.IsNull(0) ? null : value.Text(0), value.IsNull(1) ? null : value.Text(1),
                value.IsNull(2) ? null : value.Text(2), value.IsNull(3) ? null : value.Text(3), value.IsNull(4) ? null : value.Long(4),
                value.IsNull(5) ? null : value.Text(5), value.IsNull(6) ? null : value.Text(6), value.IsNull(7) ? null : value.Text(7),
                value.IsNull(8) ? null : value.Text(8), value.IsNull(9) ? null : value.Text(9),
                value.IsNull(10) ? null : value.Text(10), value.IsNull(11) ? null : value.Text(11)),
            new("$session", sessionId), new("$start", $"start/{sessionId}/1"),
            new("$admission", HandoffAdmissionKeys.TargetAdmission(machine, sessionId)),
            new("$closed", HandoffAdmissionKeys.TargetClosed(machine, sessionId)))!;
        try
        {
            var payload = row.Payload is null ? null : JsonSerializer.Deserialize(row.Payload, EngineJson.Default.SessionStartEffect);
            var binding = payload?.Handoff;
            if (row.AdmissionJson is null && row.ClosedJson is null && binding is null)
                return new(HandoffFenceState.Ordinary, null, null);
            if (binding is null || payload is null || row.Machine != machine || row.AdmissionJson is null
                || row.Kind != "start" || row.StartSession != sessionId || row.Provider != payload.Provider
                || row.Account != binding.AccountContext || row.Native != payload.ProviderSessionId
                || string.IsNullOrWhiteSpace(row.Native) || binding.Identity.TargetMachine != machine
                || binding.Identity.HandoffId != sessionId || binding.Identity.NextEpoch != row.Epoch
                || payload.ParentId is not null || payload.RunId is not null || payload.TaskUrl is not null
                || payload.Origin is not null || payload.ManagedCheckout == true) return Unknown();
            _ = HandoffAdmissionKeys.Identity(binding.Identity);
            var admission = DecodeHandoffReceipt(row.AdmissionFingerprint!, row.AdmissionJson);
            if (!admission.Result.Applied || admission.Result.Epoch != binding.Identity.NextEpoch
                || admission.Fingerprint != binding.AdmissionFingerprint
                || !RelayHash.IsSha(binding.SourceAcceptFingerprint) || !RelayHash.IsSha(binding.TransferCaptureSha256)) return Unknown();
            var original = new AdoptSession(payload.Provider, payload.Model, payload.Folder, payload.Title,
                payload.ProviderSessionId!, binding.Identity.SourceMachine, binding.Identity.SourceSessionId,
                binding.Identity.NextEpoch, payload.RepoUrl);
            var canonical = HandoffAdmitCanonical(binding.Identity, original, binding.AccountContext,
                binding.SourceAcceptFingerprint, binding.TransferCaptureSha256);
            if (Fingerprint(HandoffAdmissionKeys.TargetContext(binding.Identity, false, 0), canonical) != admission.Fingerprint)
                return Unknown();
            if (row.ClosedJson is not null)
            {
                var closed = DecodeHandoffReceipt(row.ClosedFingerprint!, row.ClosedJson);
                if (!closed.Result.Applied || closed.Result.Epoch != binding.Identity.NextEpoch || closed.Result.Seq != 0
                    || closed.Fingerprint != Fingerprint(HandoffAdmissionKeys.TargetContext(binding.Identity, true, 0),
                        ClosedCanonical(binding, payload))) return Unknown();
                return new(HandoffFenceState.Closed, binding, null);
            }
            return new(HandoffFenceState.Adopted, binding, null);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return Unknown(); }
    }

    internal static string HandoffAdmitCanonical(HandoffIdentity identity, AdoptSession command, string account,
        string sourceFingerprint, string capture)
    {
        var prepared = PreparedSessionTitleCommand.Prepare(command);
        return HandoffCanonical(json =>
        {
            json.WriteString("domain", "handoff-admit/v1"); json.WritePropertyName("identity");
            JsonSerializer.Serialize(json, identity, EngineJson.Default.HandoffIdentity);
            json.WritePropertyName("command"); json.WriteRawValue(prepared.CanonicalJournal);
            json.WriteString("accountContext", account); json.WriteString("sourceAcceptFingerprint", sourceFingerprint);
            json.WriteString("transferCaptureSha256", capture);
        });
    }

    internal static string HandoffFingerprint(CommandContext context, string canonical) => Fingerprint(context, canonical);

    internal static CommandResult CommitHandoffAdopt(IWriteScope scope, HandoffAdmission admission, long version,
        IReadOnlyList<EventDraft> events, IReadOnlyList<EffectDraft> effects)
    {
        admission.ValidateBindings(scope, admission.Context, admission.Key, admission.Canonical);
        var identity = admission.Identity;
        if (admission.Operation != HandoffOperation.Adopt || admission.Binding is null || version != 1
            || admission.Binding.AdmissionFingerprint != Fingerprint(admission.Context, admission.Canonical)
            || effects.Count != 1 || effects[0].Kind != "start" || effects[0].SessionId != identity.HandoffId
            || effects[0].RunId is not null || effects[0].Id != $"start/{identity.HandoffId}/1")
            throw new InvalidOperationException("Adopt requires exactly its one original bound start effect.");
        var payload = effects[0].Payload.Deserialize(EngineJson.Default.SessionStartEffect)
            ?? throw new InvalidOperationException("Missing adopted start payload.");
        if (payload.Handoff != admission.Binding || string.IsNullOrWhiteSpace(payload.ProviderSessionId)
            || payload.ParentId is not null || payload.RunId is not null || payload.TaskUrl is not null || payload.Origin is not null
            || scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE id=$id AND machine=$machine AND holder='local'
                    AND epoch=$epoch AND state='starting' AND provider=$provider AND account_context=$account
                    AND provider_session_id=$native
                """, row => row.Long(0), new("$id", identity.HandoffId), new("$machine", identity.TargetMachine),
                new("$epoch", identity.NextEpoch), new("$provider", payload.Provider), new("$account", admission.Binding.AccountContext),
                new("$native", payload.ProviderSessionId)) != 1)
            throw new InvalidOperationException("Adopt row and original start binding disagree.");
        return CommitCore(scope, admission.Context, version, identity.NextEpoch, events, effects, false, admission);
    }

    private static string ClosedCanonical(HandoffStartBinding binding, SessionStartEffect payload) => HandoffCanonical(json =>
    {
        json.WriteString("domain", "handoff-closed/v1"); json.WritePropertyName("identity");
        JsonSerializer.Serialize(json, binding.Identity, EngineJson.Default.HandoffIdentity);
        json.WriteString("admissionFingerprint", binding.AdmissionFingerprint);
        json.WriteString("machine", binding.Identity.TargetMachine); json.WriteString("provider", payload.Provider);
        json.WriteString("accountContext", binding.AccountContext); json.WriteString("providerSessionId", payload.ProviderSessionId);
        json.WriteNumber("epoch", binding.Identity.NextEpoch);
    });

    private static string HandoffCanonical(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes)) { json.WriteStartObject(); write(json); json.WriteEndObject(); }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static HandoffReceipt DecodeHandoffReceipt(string fingerprint, string text)
    {
        var result = JsonSerializer.Deserialize(text, EngineJson.Default.CommandResult) ?? throw new JsonException();
        if (JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) != text) throw new JsonException();
        return new(fingerprint, text, result);
    }

    private static bool PhaseReceiptMatches(HandoffIdentity identity, HandoffPhase phase, bool accepted, string? reason, HandoffReceipt receipt)
    {
        var context = HandoffAdmissionKeys.SourceContext(identity, phase, accepted, 0);
        var command = new HandoffOutcome(phase == HandoffPhase.Accept ? "accepted" : phase == HandoffPhase.Resumed ? "resumed" : "failed", identity.NextEpoch, reason);
        var canonical = JsonSerializer.Serialize<EngineCommand>(command, EngineJson.Default.EngineCommand);
        return receipt.Fingerprint == Fingerprint(context, canonical) && receipt.Result.Epoch == identity.NextEpoch;
    }

    private static bool TargetAuthorizes(HandoffPhaseArgs args)
    {
        var evidence = args.Target;
        if (evidence is null || evidence.Identity != args.Identity || evidence.Epoch != args.Identity.NextEpoch
            || !RelayHash.IsSha(evidence.AdmissionFingerprint)) return false;
        if (args.Phase == HandoffPhase.Failed && evidence.State == HandoffTargetState.NotAdopted)
            return evidence.AdmissionFingerprint == Fingerprint(HandoffAdmissionKeys.TargetContext(args.Identity, false, 0),
                HandoffAdmission.NoAdoptCanonical(args.Identity)) && evidence.ClosedFingerprint is null
                && evidence.SessionVersion == 0 && evidence.StartEffectId is null && evidence.StartAttempt is null;
        if (evidence.SessionVersion < 1 || evidence.StartAttempt is not > 0
            || evidence.StartEffectId != $"start/{args.Identity.HandoffId}/{evidence.StartAttempt}") return false;
        return args.Phase == HandoffPhase.Resumed && evidence.State == HandoffTargetState.Resumed
            && evidence.ResumeReason is null && evidence.ClosedFingerprint is null
            || args.Phase == HandoffPhase.Failed && evidence.State == HandoffTargetState.Failed
            && RelayHash.IsSha(evidence.ClosedFingerprint) && evidence.ResumeReason == args.Reason;
    }

    private static bool ValidIdentity(IReadScope scope, string machine, HandoffIdentity identity)
    {
        try { _ = HandoffAdmissionKeys.Identity(identity); }
        catch (ArgumentException) { return false; }
        return (machine == identity.SourceMachine || machine == identity.TargetMachine)
            && scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) == machine;
    }

    private static HandoffSource? Source(IReadScope scope, HandoffIdentity identity) => scope.QueryFirst("""
        SELECT h.version,h.state,h.reason,h.machine,h.session_id,h.old_holder,h.new_holder,h.old_epoch,h.next_epoch,
            s.machine,s.holder,s.epoch,s.reserved_epoch,s.state
        FROM engine_handoff h JOIN engine_session s ON s.id=h.session_id WHERE h.id=$id
        """, row => new HandoffSource(row.Long(0), row.Text(1), row.IsNull(2) ? null : row.Text(2), row.Text(3), row.Text(4),
            row.Text(5), row.Text(6), row.Long(7), row.Long(8), row.Text(9), row.Text(10), row.Long(11),
            row.IsNull(12) ? null : row.Long(12), row.Text(13)), new SqlArg("$id", identity.HandoffId));

    private static bool Matches(HandoffSource source, HandoffIdentity identity) => source.Machine == identity.SourceMachine
        && source.SessionMachine == identity.SourceMachine && source.SessionId == identity.SourceSessionId
        && (source.OldHolder == "local" ? source.Machine : source.OldHolder) == identity.SourceMachine
        && source.Target == identity.TargetMachine && source.OldEpoch == identity.OldEpoch && source.NextEpoch == identity.NextEpoch;

    private static bool HasReceipt(IReadScope scope, string key) => scope.QueryFirst(
        "SELECT EXISTS(SELECT 1 FROM engine_command_receipt WHERE command_id=$key)", row => row.Long(0), new SqlArg("$key", key)) != 0;

    private static HandoffReceipt? ReadReceipt(IReadScope scope, string key) => scope.QueryFirst(
        "SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$key", row =>
        {
            var text = row.Text(1); var result = JsonSerializer.Deserialize(text, EngineJson.Default.CommandResult) ?? throw new JsonException();
            if (JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) != text) throw new JsonException();
            return new HandoffReceipt(row.Text(0), text, result);
        }, new SqlArg("$key", key));

    private static HandoffAck Ack(HandoffIdentity identity, HandoffGate gate, string? reason, HandoffSource? source = null,
        string? key = null, HandoffReceipt? receipt = null) => new(identity, gate, reason, key, receipt?.Fingerprint,
            receipt?.Result, source?.Version, source?.State, null);
}
