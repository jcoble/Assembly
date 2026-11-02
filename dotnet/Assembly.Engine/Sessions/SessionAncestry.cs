using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

internal sealed record AncestryNode(string Id, string Machine, string State, string? ParentId,
    long Version, long Epoch, string Holder, long Managed);
internal sealed record AncestryChain(bool Known, IReadOnlyList<AncestryNode> Nodes);

/// <summary>One recursive FK read and one batched private-marker read on the caller's snapshot.</summary>
internal static class SessionAncestry
{
    internal static AncestryChain Read(IReadScope scope, string target, string sessionId)
    {
        var rows = scope.Query("""
            WITH RECURSIVE ancestors(id,machine,state,parent_id,version,epoch,holder,managed_checkout) AS (
                SELECT id,machine,state,parent_id,version,epoch,holder,managed_checkout FROM engine_session WHERE id=$id
                UNION
                SELECT p.id,p.machine,p.state,p.parent_id,p.version,p.epoch,p.holder,p.managed_checkout
                FROM engine_session p JOIN ancestors a ON p.id=a.parent_id
            ) SELECT id,machine,state,parent_id,version,epoch,holder,managed_checkout FROM ancestors
            """, r => new AncestryNode(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3),
                r.Long(4), r.Long(5), r.Text(6), r.Long(7)), new SqlArg("$id", sessionId));
        var foreign = rows.Where(n => n.State == "foreign" || n.Machine != target).ToArray();
        var parameters = foreign.Select((n, i) => new SqlArg("$key" + i, RelayReceiptKeys.ForeignParent(target, n.Id))).ToArray();
        var receipts = parameters.Length == 0 ? new Dictionary<string, (string Hash, string Result)>(StringComparer.Ordinal)
            : scope.Query($"SELECT command_id,fingerprint,result_json FROM engine_command_receipt WHERE command_id IN ({string.Join(',', parameters.Select(p => p.Name))})",
                r => (Key: r.Text(0), Hash: r.Text(1), Result: r.Text(2)), parameters)
                .ToDictionary(r => r.Key, r => (r.Hash, r.Result), StringComparer.Ordinal);
        var known = rows.Count > 0;
        foreach (var node in foreign)
        {
            var key = RelayReceiptKeys.ForeignParent(target, node.Id);
            if (node.Machine == target || node.State != "foreign" || node.Version != 0 || node.Epoch != 0 ||
                node.Holder != node.Machine || node.Managed != 0 || !receipts.TryGetValue(key, out var receipt) ||
                !EngineJournal.IsForeignParentProof(target, new(node.Id, node.Machine, "", "", node.ParentId), receipt.Hash, receipt.Result))
                known = false;
        }
        var byId = rows.ToDictionary(n => n.Id, StringComparer.Ordinal);
        List<AncestryNode> chain = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? next = sessionId;
        while (next is not null)
        {
            if (!visited.Add(next) || !byId.TryGetValue(next, out var node)) { known = false; break; }
            chain.Add(node); next = node.ParentId;
        }
        return new(known, chain);
    }
}
