using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    internal ForeignTitleSourceCapture ForeignTitleSource(ForeignTitleSourceOpenArgs args, CancellationToken ct)
    {
        using var scope = db.OpenSnapshot(ct);
        if (!Enum.IsDefined(args.Mode) || string.IsNullOrWhiteSpace(args.AnchorSessionId) || string.IsNullOrWhiteSpace(args.TargetMachine) ||
            scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != machine)
            throw ForeignTitleRefusal("bad_args");
        var chain = SessionAncestry.Read(scope, machine, args.AnchorSessionId);
        if (!chain.Known || chain.Nodes[0].Machine != machine || chain.Nodes[0].State == "foreign")
            throw ForeignTitleRefusal("forbidden");
        var selected = args.Mode == ForeignTitleSourceMode.NodeOnly ? [args.AnchorSessionId] :
            chain.Nodes.Reverse().Where(n => n.Machine == machine && n.State != "foreign").Select(n => n.Id).ToArray();
        var rows = scope.Query("""
            SELECT s.id,s.machine,s.provider,s.parent_id,CAST(s.title AS BLOB),s.epoch,s.holder,s.reserved_epoch,s.state
            FROM engine_session s WHERE s.machine=$machine AND s.holder='local' AND s.state<>'foreign'
                AND s.id IN (SELECT value FROM json_each($ids))
                AND $machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
            """, r => new ForeignTitleRow(new(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.Long(5), r.Text(6), r.IsNull(7) ? null : r.Long(7)), r.IsNull(4) ? null : r.Blob(4)),
            new("$machine", machine), new("$ids", JsonSerializer.Serialize<IReadOnlyList<string>>(selected, EngineJson.Default.IReadOnlyListString)))
            .ToDictionary(r => r.Owner.Id, StringComparer.Ordinal);
        if (rows.Count != selected.Length) throw ForeignTitleRefusal("source_changed");
        var nodes = new ForeignAncestor[selected.Length]; var titles = new byte[selected.Length][];
        var ownership = new ForeignTitleOwnership[selected.Length];
        try
        {
            for (var i = 0; i < selected.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); var row = rows[selected[i]]; var owner = row.Owner;
                if (row.Title is null || string.IsNullOrWhiteSpace(owner.Provider) || owner.Epoch < 0 || owner.ReservedEpoch < 0)
                    throw ForeignTitleRefusal("bad_args");
                titles[i] = row.Title; ownership[i] = owner;
                nodes[i] = new(owner.Id, owner.Machine, owner.Provider, RelayHash.Utf8.GetString(row.Title), owner.ParentId);
            }
        }
        catch (DecoderFallbackException) { throw ForeignTitleRefusal("bad_parts"); }
        ct.ThrowIfCancellationRequested();
        return new(args, machine, nodes, titles, ownership);
    }

    internal ForeignTitleSourceChecked CheckForeignTitleSource(ForeignTitleSourceCapture capture, CancellationToken ct)
    {
        using var scope = db.OpenSnapshot(ct);
        if (capture.Manifest.SourceMachine != machine) throw ForeignTitleRefusal("source_changed");
        var ids = capture.Ownership.Select(n => n.Id).ToArray();
        var rows = scope.Query("""
            SELECT id,machine,provider,parent_id,epoch,holder,reserved_epoch FROM engine_session
            WHERE machine=$machine AND holder='local' AND state<>'foreign'
                AND id IN (SELECT value FROM json_each($ids))
                AND $machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
            """, r => new ForeignTitleOwnership(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.Long(4), r.Text(5), r.IsNull(6) ? null : r.Long(6)), new("$machine", machine),
            new("$ids", JsonSerializer.Serialize<IReadOnlyList<string>>(ids, EngineJson.Default.IReadOnlyListString)))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (rows.Count != capture.Ownership.Count || capture.Ownership.Any(n => !rows.TryGetValue(n.Id, out var row) || row != n))
            throw ForeignTitleRefusal("source_changed");
        ct.ThrowIfCancellationRequested(); return capture.Checked();
    }

    private static ForeignAncestryRefused ForeignTitleRefusal(string error) => new(new(false, false, error, 0, 0, 0));
    private sealed record ForeignTitleRow(ForeignTitleOwnership Owner, byte[]? Title);
}
