using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static HandoffAck ExecuteNativeHandoffAdopt(IWriteScope scope, string machine, PreparedNativeAdoption prepared,
        long nowMs, Action<IWriteScope, string, string?> project)
    {
        var args = prepared.Args;
        var identity = args.Identity;
        var preflight = ExecuteHandoffAdopt(scope, machine, args, nowMs);
        if (preflight.Gate != HandoffGate.Waiting || preflight.Reason != "native_witness_required") return preflight;
        var command = args.Command;
        var witness = prepared.Capture.Current;
        if (witness.SessionId != identity.HandoffId || witness.MachineId != machine || witness.Epoch != identity.NextEpoch ||
            witness.ProviderId != command.Provider || witness.AcpSessionId != command.ProviderSessionId ||
            witness.ResumeId != command.ProviderSessionId || witness.Folder != command.Folder ||
            witness.AccountContext != prepared.Launch.AccountContext || string.IsNullOrWhiteSpace(command.Title) ||
            !Path.IsPathFullyQualified(command.Folder)) return Ack(identity, HandoffGate.Refused, "native_witness_mismatch");
        var account = witness.AccountContext;
        // SQL excludes structurally closed history, but never uses that history as disposal proof.
        var current = scope.Query("""
            SELECT s.id FROM engine_session s
            LEFT JOIN engine_command_receipt closed ON closed.command_id='handoff-target/v1/' ||
                lower(hex(CAST(s.machine AS BLOB))) || '/' || lower(hex(CAST(s.id AS BLOB))) || '/closed'
            LEFT JOIN engine_command_receipt admitted ON admitted.command_id='handoff-target/v1/' ||
                lower(hex(CAST(s.machine AS BLOB))) || '/' || lower(hex(CAST(s.id AS BLOB))) || '/admission'
            LEFT JOIN engine_outbox first ON first.id='start/' || s.id || '/1'
            WHERE s.machine=$machine AND s.provider=$provider AND s.account_context=$account AND s.provider_session_id=$native
              AND NOT (closed.command_id IS NOT NULL AND admitted.command_id IS NOT NULL AND first.id IS NOT NULL
                AND s.state IN ('ended','lost') AND json_extract(closed.result_json,'$.applied')=1
                AND json_extract(closed.result_json,'$.epoch')=s.epoch AND json_extract(admitted.result_json,'$.applied')=1
                AND json_extract(admitted.result_json,'$.epoch')=s.epoch
                AND json_extract(first.payload_json,'$.handoff.admissionFingerprint')=admitted.fingerprint
                AND json_extract(first.payload_json,'$.handoff.identity.targetMachine')=s.machine
                AND json_extract(first.payload_json,'$.handoff.identity.handoffId')=s.id
                AND json_extract(first.payload_json,'$.handoff.identity.nextEpoch')=s.epoch
                AND json_extract(first.payload_json,'$.handoff.accountContext')=s.account_context
                AND json_extract(first.payload_json,'$.provider')=s.provider
                AND json_extract(first.payload_json,'$.providerSessionId')=s.provider_session_id)
            LIMIT 2
            """, r => r.Text(0), new("$machine", machine), new("$provider", command.Provider), new("$account", account),
            new("$native", command.ProviderSessionId));
        if (current.Count != 0 || scope.QueryFirst("""
            SELECT EXISTS(SELECT 1 FROM engine_outbox o JOIN engine_session s ON s.id=o.session_id
                WHERE s.machine=$machine AND s.provider=$provider AND s.account_context=$account AND s.provider_session_id=$native
                  AND o.state NOT IN ('done','failed','dead'))
            """, r => r.Long(0) != 0, new SqlArg("$machine", machine), new("$provider", command.Provider), new("$account", account),
            new("$native", command.ProviderSessionId))) return Ack(identity, HandoffGate.Waiting, "writer_elsewhere");
        var admission = HandoffAdmission.Adopt(scope, args, account, nowMs);
        ExecuteCore(scope, admission.Context, admission.Key, admission.Canonical, () =>
        {
            scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,
                    state,activity_state,holder,epoch,cancel_requested,account_context,provider_session_id,managed_checkout)
                VALUES($id,1,$machine,$now,$now,$provider,$model,$folder,CAST($title AS TEXT),
                    'starting','idle','local',$epoch,0,$account,$native,0)
                """, new("$id", identity.HandoffId), new("$machine", machine), new("$now", nowMs),
                new("$provider", command.Provider), new("$model", command.Model), new("$folder", command.Folder),
                new("$title", new UTF8Encoding(false, true).GetBytes(command.Title)), new("$epoch", identity.NextEpoch),
                new("$account", account), new("$native", command.ProviderSessionId));
            project(scope, identity.HandoffId, command.RepoUrl);
            var payload = new SessionStartEffect(command.Provider, command.Model, command.Folder, command.Title,
                null, null, null, command.RepoUrl, ProviderSessionId: command.ProviderSessionId, Handoff: admission.Binding);
            return CommitHandoffAdopt(scope, admission, 1,
                [new("session.started", "session", identity.HandoffId, 1, false, new(identity.HandoffId, null, null, null, "starting", identity.NextEpoch))],
                [new("start/" + identity.HandoffId + "/1", "start", identity.HandoffId, null,
                    JsonSerializer.SerializeToElement(payload, EngineJson.Default.SessionStartEffect))]);
        }, handoffAdmission: admission);
        return ReadTargetAck(scope, identity);
    }

    internal static bool ValidNativeStart(IReadScope scope, string machine, Effect effect, EffectReceipt receipt)
    {
        var start = effect.Payload.Deserialize(EngineJson.Default.SessionStartEffect);
        if (start?.Handoff is null) return true;
        var fence = ReadTargetHandoffFence(scope, machine, effect.SessionId!);
        return fence.State == HandoffFenceState.Adopted && fence.Binding == start.Handoff &&
            ProvesNativeStart(machine, effect, start, receipt.Data.Deserialize(EngineJson.Default.SessionEffectData));
    }

    // Resumed needs the actual adapter's capture for this exact session/epoch. Ended needs its typed refusal and claims no capture.
    private static bool ProvesNativeStart(string machine, Effect effect, SessionStartEffect start, SessionEffectData? data)
    {
        if (data is null || data.ProviderSessionId != start.ProviderSessionId) return false;
        if (data.State == "ended") return data.NativeResume is null && data.Reason is ResumeReason.Unsupported or ResumeReason.Missing
            or ResumeReason.Denied or ResumeReason.WriterElsewhere or ResumeReason.NoFolder;
        return data.State == "idle" && data.NativeResume is { Capture.Current: { } native } proof &&
            Guid.TryParseExact(proof.OperationId, "D", out _) && native.SessionId == effect.SessionId &&
            native.MachineId == machine && native.Epoch == start.Handoff!.Identity.NextEpoch && native.ProviderId == start.Provider &&
            native.AccountContext == start.Handoff.AccountContext && native.AcpSessionId == start.ProviderSessionId &&
            native.ResumeId == start.ProviderSessionId && native.Folder == start.Folder && proof.Folder == start.Folder;
    }

    /// <summary>The only source of adopted target evidence: the first start's own completed, checked receipt.</summary>
    private static HandoffAck ReadAdoptedTarget(IReadScope scope, HandoffIdentity identity, string key, HandoffReceipt receipt, HandoffTargetFence fence)
    {
        var startId = $"start/{identity.HandoffId}/1";
        var row = scope.QueryFirst("""
            SELECT o.id,o.kind,o.session_id,o.run_id,o.payload_json,o.attempt,o.order_key,o.state,o.receipt_json,s.version
            FROM engine_outbox o JOIN engine_session s ON s.id=o.session_id WHERE o.id=$id
            """, r => new
        {
            Effect = new Effect(r.Text(0), r.Text(1), r.IsNull(2) ? null : r.Text(2), r.IsNull(3) ? null : r.Text(3),
                JsonDocument.Parse(r.Text(4)).RootElement.Clone(), checked((int)r.Long(5)), r.Text(6)),
            State = r.Text(7),
            Receipt = r.IsNull(8) ? null : r.Text(8),
            Version = r.Long(9)
        }, new SqlArg("$id", startId));
        var done = row is { State: "done", Receipt: not null } ? JsonSerializer.Deserialize(row.Receipt, EngineJson.Default.EffectReceipt) : null;
        var start = row?.Effect.Payload.Deserialize(EngineJson.Default.SessionStartEffect);
        var data = done is { Disposition: EffectDisposition.Confirmed } ? done.Data.Deserialize(EngineJson.Default.SessionEffectData) : null;
        var proved = data is not null && start?.Handoff == fence.Binding && ProvesNativeStart(identity.TargetMachine, row!.Effect, start!, data);
        var closed = fence.State == HandoffFenceState.Closed ? ReadReceipt(scope, HandoffAdmissionKeys.TargetClosed(identity.TargetMachine, identity.HandoffId)) : null;
        if (fence.State == HandoffFenceState.Closed && (!proved || data!.State != "ended"))
            return Ack(identity, HandoffGate.Unknown, "adopted_operation_evidence_required");
        if (!proved) return new(identity, HandoffGate.Recorded, "operation_evidence_required", key, receipt.Fingerprint, receipt.Result, null, null, null);
        var ended = data!.State == "ended";
        return new(identity, HandoffGate.Recorded, ended ? data.Reason : null, key, receipt.Fingerprint, receipt.Result, null, null,
            new(identity, ended ? HandoffTargetState.Failed : HandoffTargetState.Resumed, receipt.Fingerprint, closed?.Fingerprint,
                row!.Version, identity.NextEpoch, ended ? data.Reason : null, startId, 1));
    }

    private static HandoffAck CloseFailedTarget(IWriteScope scope, HandoffSealArgs args, HandoffAck prior, long nowMs)
    {
        var identity = args.Identity;
        if (prior.Target is not { State: HandoffTargetState.Failed, ClosedFingerprint: null } evidence || evidence.ResumeReason != args.Reason ||
            scope.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE session_id=$id AND state NOT IN ('done','failed','dead'))",
                r => r.Long(0), new SqlArg("$id", identity.HandoffId)) != 0) return prior;
        var fence = ReadTargetHandoffFence(scope, identity.TargetMachine, identity.HandoffId);
        var payload = scope.QueryFirst("SELECT payload_json FROM engine_outbox WHERE id=$id", r => r.Text(0), new SqlArg("$id", $"start/{identity.HandoffId}/1"));
        var start = payload is null ? null : JsonSerializer.Deserialize(payload, EngineJson.Default.SessionStartEffect);
        if (fence.State != HandoffFenceState.Adopted || fence.Binding is null || start is null) return prior;
        var admission = HandoffAdmission.Closed(scope, identity, ClosedCanonical(fence.Binding, start), nowMs);
        ExecuteCore(scope, admission.Context, admission.Key, admission.Canonical,
            () => new(true, false, null, 0, 0, identity.NextEpoch), handoffAdmission: admission);
        return ReadTargetAck(scope, identity);
    }
}
