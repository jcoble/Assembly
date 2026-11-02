using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Contracts;
using System.Text.Json;

namespace Assembly.Engine.Sessions;

internal sealed record AncestryNode(string Id, string Machine, string State, string? ParentId,
    long Version, long Epoch, string Holder, long Managed);
internal sealed record AncestryChain(bool Known, IReadOnlyList<AncestryNode> Nodes, bool InvalidProof = false, CommandResult? MarkerResult = null);

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
        var known = rows.Count > 0; var invalid = false;
        foreach (var node in foreign)
        {
            var key = RelayReceiptKeys.ForeignParent(target, node.Id);
            var validRow = node.Machine != target && node.State == "foreign" && node.Version == 0 && node.Epoch == 0 &&
                node.Holder == node.Machine && node.Managed == 0;
            if (!receipts.TryGetValue(key, out var receipt))
            {
                known = false;
                if (!validRow || node.ParentId is not null) invalid = true;
                continue;
            }
            if (!validRow || !EngineJournal.IsForeignParentProof(target, new(node.Id, node.Machine, "", "", node.ParentId), receipt.Hash, receipt.Result))
            { known = false; invalid = true; }
        }
        var byId = rows.ToDictionary(n => n.Id, StringComparer.Ordinal);
        List<AncestryNode> chain = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? next = sessionId;
        while (next is not null)
        {
            if (!visited.Add(next)) { known = false; invalid = true; break; }
            if (!byId.TryGetValue(next, out var node)) { known = false; invalid |= chain.Count > 0; break; }
            chain.Add(node); next = node.ParentId;
        }
        // This is the exact final-node result from the same already-validated batched marker snapshot.
        CommandResult? result = null;
        if (known && chain[0].Machine != target && receipts.TryGetValue(RelayReceiptKeys.ForeignParent(target, sessionId), out var final))
            result = JsonSerializer.Deserialize(final.Result, EngineJson.Default.CommandResult);
        return new(known, chain, invalid, result);
    }
}
