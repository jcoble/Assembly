using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Messages;
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
        if (row.Kind == "system" && (row.Id != "child-ended/" + row.From || row.Parent != row.To || row.SenderState != "ended" || label != "Assembly"))
            throw new RelayRefused("bad_args");
        var fence = new RelaySourceFence(row.Version, row.State, row.Attempt, row.InFlight == 1,
            row.SenderEpoch, row.SenderHolder, row.SenderReserved, row.RecipientEpoch, row.RecipientHolder);
        return RelayDigests.Capture(machine, args.RecipientMachine, row.Id, row.Kind,
            new(row.From, row.To, text, row.Mode), label, fence);
    }

    private sealed record SourceRow(string Id, long Version, string From, string Kind, string To, byte[] Body, byte[] Label,
        string Mode, string State, long Attempt, long InFlight, string Delivery, string SenderHolder, long SenderEpoch,
        long? SenderReserved, string SenderState, string? Parent, string RecipientHolder, long RecipientEpoch, long? RecipientReserved);
}
