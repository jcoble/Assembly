using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    internal RelaySourceCapture RelaySource(RelaySourceReadArgs args, CancellationToken ct)
    {
        using var scope = db.OpenSnapshot(ct);
        return ReadRelaySource(scope, machine, args);
    }

    internal static RelaySourceCapture ReadRelaySource(IReadScope scope, string machine, RelaySourceReadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.MessageId) || string.IsNullOrWhiteSpace(args.RecipientMachine) || machine == args.RecipientMachine ||
            scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != machine)
            throw new RelayRefused("bad_args");
        var row = scope.QueryFirst("""
            SELECT m.id,m.version,m.from_session_id,m.sender_kind,m.to_session_id,CAST(m.text AS BLOB),CAST(m.sender_label AS BLOB),
                m.mode,m.state,m.attempt,m.in_flight,m.delivery,f.holder,f.epoch,f.reserved_epoch,f.state,f.parent_id,
                t.holder,t.epoch,t.reserved_epoch
            FROM engine_message m JOIN engine_session f ON f.id=m.from_session_id JOIN engine_session t ON t.id=m.to_session_id
            WHERE m.id=$id AND m.machine=$machine AND f.machine=$machine AND f.state<>'foreign'
                AND t.state='foreign' AND t.machine=$target AND t.machine<>$machine AND m.framed=1
                AND m.client_id IS NULL AND m.held_epoch IS NULL AND m.target_epoch IS NULL AND m.owner_content_sha256 IS NULL
                AND json_array_length(m.attachment_ids_json)=0
            LIMIT 1
            """, r => new SourceRow(r.Text(0), r.Long(1), r.Text(2), r.Text(3), r.Text(4), r.Blob(5), r.Blob(6), r.Text(7),
                r.Text(8), r.Long(9), r.Long(10), r.Text(11), r.Text(12), r.Long(13), r.IsNull(14) ? null : r.Long(14),
                r.Text(15), r.IsNull(16) ? null : r.Text(16), r.Text(17), r.Long(18), r.IsNull(19) ? null : r.Long(19)),
            new("$id", args.MessageId), new("$machine", machine), new("$target", args.RecipientMachine));
        if (row is null || row.InFlight is not (0 or 1) || row.Delivery is not ("wait" or "steer") || row.SenderState == "handing_off" ||
            row.RecipientReserved is not null || row.Kind is not ("session" or "system")) throw new RelayRefused("bad_args");
        var text = RelayHash.Utf8.GetString(row.Body);
        var label = RelayHash.Utf8.GetString(row.Label);
        if (row.Kind == "system")
        {
            var ended = row.Id == "child-ended/" + row.From && row.SenderState == "ended";
            var need = row.Id.StartsWith("need/", StringComparison.Ordinal) && scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_needs_me WHERE id=$need AND session_id=$child AND machine=$machine
                """, r => r.Long(0), new SqlArg("$need", row.Id[5..]), new("$child", row.From), new("$machine", machine)) == 1;
            if (row.Parent != row.To || label != "Assembly" || !ended && !need) throw new RelayRefused("bad_args");
        }
        var evidence = ReadTurnReplyEvidence(scope, machine, row, text);
        var fence = new RelaySourceFence(row.Version, row.State, row.Attempt, row.InFlight == 1,
            row.SenderEpoch, row.SenderHolder, row.SenderReserved, row.RecipientEpoch, row.RecipientHolder);
        return RelayDigests.Capture(machine, args.RecipientMachine, row.Id, row.Kind,
            new(row.From, row.To, text, row.Mode), label, fence, evidence);
    }

    private static RelayTurnReplyEvidence? ReadTurnReplyEvidence(IReadScope scope, string machine, SourceRow row, string body)
    {
        if (!row.Id.StartsWith("turn-reply/", StringComparison.Ordinal)) return null;
        var suffix = row.Id["turn-reply/".Length..];
        if (!RelayHash.IsSha(suffix) || row.Parent != row.To || row.Kind != "session" || row.Mode != "prompt") throw new RelayRefused("source_changed");
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new { Fingerprint = r.Text(0), Result = r.Text(1) }, new SqlArg("$id", "turn-reply/v1/" + suffix));
        if (receipt is null || !TurnReplyFingerprint.TryDecode(receipt.Fingerprint, machine, row.From, row.Id, out var carrier)) throw new RelayRefused("source_changed");
        try
        {
            var result = JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult);
            if (result is not { Applied: true, Late: false, Error: null, Version: 1, Seq: > 0, Epoch: >= 0 } ||
                JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) != receipt.Result) throw new RelayRefused("source_changed");
            var run = scope.QueryFirst("SELECT CAST(run_id AS BLOB) FROM rows WHERE session_id=$child AND seq=$seq AND kind='turn'",
                r => r.IsNull(0) ? null : RelayHash.Utf8.GetString(r.Blob(0)), new SqlArg("$child", row.From), new SqlArg("$seq", carrier!.TerminalSeq));
            if (run is null || RelayHash.Sha(RelayHash.Utf8.GetBytes(run)) != carrier!.ChatRunIdSha256 ||
                PreparedTurnReply.MessageId(machine, row.From, run) != row.Id ||
                RowStore.TerminalReplyAt(scope, row.From, run, carrier.TerminalSeq)?.Text != body) throw new RelayRefused("source_changed");
            return new(run, carrier.TerminalSeq, receipt.Fingerprint);
        }
        catch (Exception error) when (error is JsonException or System.Text.DecoderFallbackException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new RelayRefused("source_changed"); }
    }

    private sealed record SourceRow(string Id, long Version, string From, string Kind, string To, byte[] Body, byte[] Label,
        string Mode, string State, long Attempt, long InFlight, string Delivery, string SenderHolder, long SenderEpoch,
        long? SenderReserved, string SenderState, string? Parent, string RecipientHolder, long RecipientEpoch, long? RecipientReserved);
}
