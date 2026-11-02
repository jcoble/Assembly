using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static RelaySendAck ReadRelayAck(IReadScope scope, string machine, RelaySourceDescriptor source) =>
        ReadRelayState(scope, machine, source).Ack;

    internal static RelaySendAck ApplyRelaySend(IWriteScope scope, string machine, RelaySendRequest request,
        PreparedRelaySend? prepared, Func<int> messageLimit, MessageExpiryPolicy policy, long nowMs)
    {
        var probe = ProbeRelaySend(scope, machine, request);
        if (probe.Status != "parts_required") return probe.Ack;
        if (prepared is null) throw new RelayRefused("parts_required");
        if (prepared.Source != request.Source) throw new RelayRefused("source_changed");
        var retry = request.PredecessorRefusalKey is not null;
        var key = retry ? RetryKey(request.Source) : RootKey(request.Source);
        var context = RelayContext(machine, request.Source, key, request.PreparedRecipientEpoch, nowMs);
        var admission = RelaySendAdmission.Create(scope, context, key, request.Source, prepared, messageLimit, policy);
        ExecuteCore(scope, context, key, admission.Canonical, admission.Transition, admission);
        return ReadRelayState(scope, machine, request.Source).Ack;
    }

    internal static RelaySendBeginReply ProbeRelaySend(IReadScope scope, string machine, RelaySendRequest request)
    {
        RelayDigests.Validate(request.Source);
        if (request.Source.RecipientMachine != machine || request.PreparedRecipientEpoch < 0 ||
            request.PredecessorRefusalKey is not null && request.PredecessorRefusalKey != RootKey(request.Source))
            throw new RelayRefused("bad_args");
        var state = ReadRelayState(scope, machine, request.Source);
        if (state.Ack.Unknown) return new("unknown", null, state.Ack);
        var retry = request.PredecessorRefusalKey is not null;
        var selected = retry ? state.Retry : state.Root;
        if (selected is not null && selected.RequestedEpoch != request.PreparedRecipientEpoch)
            throw new RelayRefused("command_id_conflict");
        if (state.Ack.DestinationPresent) return new("recorded", null, state.Ack);
        if (retry && state.Root?.Result is not { Applied: false, Error: "stale_epoch" })
            throw new RelayRefused("bad_args");
        if (selected is not null) return new("recorded", null, state.Ack);
        if (!RelayDigests.CanSend(request.Source) || state.Target is not { Holder: "local", ReservedEpoch: null } ||
            state.Target.State == "handing_off") return new("unknown", null, state.Ack with { Unknown = true });
        return new("parts_required", null, state.Ack);
    }

    private static string RootKey(RelaySourceDescriptor source) => "relay-send/v1/" + source.IdentitySha256 + "/root";
    private static string RetryKey(RelaySourceDescriptor source) => "relay-send/v1/" + source.IdentitySha256 + "/retry";
    private static CommandContext RelayContext(string machine, RelaySourceDescriptor source, string key, long epoch, long nowMs = 0) =>
        new(RelayReceiptKeys.ContextGuid(key), machine, new(ActorKind.System, "machine:" + source.FromMachine), source.MessageId, 0, epoch, nowMs);

    private static RelayReadState ReadRelayState(IReadScope scope, string machine, RelaySourceDescriptor source)
    {
        RelayDigests.Validate(source);
        var rootKey = RootKey(source); var retryKey = RetryKey(source);
        var canonical = RelayDigests.Canonical(source);
        var rows = scope.Query("SELECT command_id,fingerprint,result_json FROM engine_command_receipt WHERE command_id IN ($root,$retry)",
            r => (Key: r.Text(0), Fingerprint: r.Text(1), Result: r.Text(2)), new("$root", rootKey), new("$retry", retryKey));
        var unknown = source.RecipientMachine != machine || scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != machine;
        RelayReceipt? root = null, retry = null;
        foreach (var row in rows)
        {
            if (!RelayFingerprint.TryDecode(row.Fingerprint, out var epoch, out var hash) ||
                hash != Fingerprint(RelayContext(machine, source, row.Key, epoch), canonical) || !TryRelayResult(row.Result, out var result))
            { unknown = true; continue; }
            var receipt = new RelayReceipt(epoch, result!);
            if (row.Key == rootKey) root = receipt; else retry = receipt;
        }
        if (retry is not null && root?.Result is not { Applied: false, Error: "stale_epoch" }) unknown = true;
        var target = ReadRelayTarget(scope, machine, source);
        if (target is null) unknown = true;
        RelayDestinationEvidence? evidence = target is null ? null : new(source.IdentitySha256, source.SourceSha256, false,
            null, null, null, null, target.Epoch, target.Holder, target.ReservedEpoch);
        if (target?.Message is { } message)
        {
            if (message.Machine != machine || message.From != source.FromSessionId || message.To != source.ToSessionId ||
                message.Framed != 1 || !message.Ordinary) unknown = true;
            else
            {
                try
                {
                    var capture = RelayDigests.Capture(source.FromMachine, machine, source.MessageId, message.Kind,
                        new(message.From, message.To, RelayHash.Utf8.GetString(message.Body), message.Mode), RelayHash.Utf8.GetString(message.Label), source.Fence, source.ReplyEvidence);
                    var actual = RelayDigests.Describe(capture);
                    if (actual with { Fence = source.Fence } != source) unknown = true;
                    else evidence = new(actual.IdentitySha256, actual.SourceSha256, true, message.Version,
                        message.State, message.Turn, message.Reason, target.Epoch, target.Holder, target.ReservedEpoch);
                }
                catch (Exception error) when (error is RelayRefused or System.Text.DecoderFallbackException or ArgumentException)
                { unknown = true; }
            }
        }
        var present = evidence?.Present == true;
        if (!present && (root?.Result.Applied == true || retry?.Result.Applied == true)) unknown = true;
        if (present && root?.Result.Applied != true && retry?.Result.Applied != true) unknown = true;
        return new(new(source.IdentitySha256, rootKey, root?.Result, retryKey, retry?.Result, present, evidence, unknown), target, root, retry);
    }

    private static bool TryRelayResult(string json, out CommandResult? result)
    {
        result = null;
        try
        {
            result = JsonSerializer.Deserialize(json, EngineJson.Default.CommandResult);
            return result is not null && !result.Late && result.Version >= 0 && result.Seq >= 0 && result.Epoch >= 0 &&
                (result.Applied ? result.Error is null && result.Version > 0 && result.Seq > 0 : !string.IsNullOrWhiteSpace(result.Error) && result.Seq == 0) &&
                JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) == json;
        }
        catch (JsonException) { return false; }
    }

    private static RelayTarget? ReadRelayTarget(IReadScope scope, string machine, RelaySourceDescriptor source) => scope.QueryFirst("""
        SELECT t.epoch,t.holder,t.reserved_epoch,t.state,m.id,m.machine,m.from_session_id,m.sender_kind,m.to_session_id,
            CAST(m.text AS BLOB),CAST(m.sender_label AS BLOB),m.mode,m.version,m.state,m.provider_turn_id,m.reason,m.framed,
            CASE WHEN m.client_id IS NULL AND m.held_epoch IS NULL AND m.target_epoch IS NULL AND m.owner_content_sha256 IS NULL
                AND json_array_length(m.attachment_ids_json)=0 THEN 1 ELSE 0 END
        FROM engine_session f JOIN engine_session t ON t.id=$to LEFT JOIN engine_message m ON m.id=$message
        WHERE f.id=$from AND f.machine=$source AND f.state='foreign' AND f.holder=$source AND f.machine<>$machine
            AND t.machine=$machine AND t.state<>'foreign'
        """, r => new RelayTarget(r.Long(0), r.Text(1), r.IsNull(2) ? null : r.Long(2), r.Text(3), r.IsNull(4) ? null :
            new StoredRelayMessage(r.Text(5), r.IsNull(6) ? null : r.Text(6), r.Text(7), r.Text(8), r.Blob(9), r.Blob(10),
                r.Text(11), r.Long(12), r.Text(13), r.IsNull(14) ? null : r.Text(14), r.IsNull(15) ? null : r.Text(15), r.Long(16), r.Long(17) == 1)),
        new("$to", source.ToSessionId), new("$message", source.MessageId), new("$from", source.FromSessionId),
        new("$source", source.FromMachine), new("$machine", machine));

    private sealed record StoredRelayMessage(string Machine, string? From, string Kind, string To, byte[] Body, byte[] Label,
        string Mode, long Version, string State, string? Turn, string? Reason, long Framed, bool Ordinary);
    private sealed record RelayTarget(long Epoch, string Holder, long? ReservedEpoch, string State, StoredRelayMessage? Message);
    private sealed record RelayReceipt(long RequestedEpoch, CommandResult Result);
    private sealed record RelayReadState(RelaySendAck Ack, RelayTarget? Target, RelayReceipt? Root, RelayReceipt? Retry);

    private sealed class RelaySendAdmission
    {
        private readonly IWriteScope _scope;
        private readonly CommandContext _context;
        private readonly string _key;
        private readonly RelaySourceDescriptor _source;
        private readonly PreparedRelaySend? _prepared;
        private readonly Func<int> _messageLimit;
        private readonly MessageExpiryPolicy _policy;
        internal string Canonical { get; }
        private RelaySendAdmission(IWriteScope scope, CommandContext context, string key, RelaySourceDescriptor source,
            PreparedRelaySend? prepared, Func<int> messageLimit, MessageExpiryPolicy policy)
        {
            _scope = scope; _context = context; _key = key; _source = source; _prepared = prepared;
            _messageLimit = messageLimit; _policy = policy; Canonical = RelayDigests.Canonical(source);
        }
        internal static RelaySendAdmission Create(IWriteScope scope, CommandContext context, string key, RelaySourceDescriptor source,
            PreparedRelaySend? prepared, Func<int> messageLimit, MessageExpiryPolicy policy) => new(scope, context, key, source, prepared, messageLimit, policy);
        internal void ValidateBindings(IWriteScope scope, CommandContext context, string key, string canonical)
        {
            if (!ReferenceEquals(scope, _scope) || context != _context || key != _key || canonical != Canonical ||
                key != RootKey(_source) && key != RetryKey(_source) || context.Machine != _source.RecipientMachine ||
                context.Actor != new EngineActor(ActorKind.System, "machine:" + _source.FromMachine) || context.EntityId != _source.MessageId ||
                context.ExpectedVersion != 0 || context.CommandId != RelayReceiptKeys.ContextGuid(key) ||
                _prepared is not null && _prepared.Source != _source) throw new RelayRefused("forbidden");
        }
        internal CommandResult Transition() => (_prepared ?? throw new RelayRefused("parts_required"))
            .Apply(_scope, _context, _messageLimit(), _policy);
    }
}
