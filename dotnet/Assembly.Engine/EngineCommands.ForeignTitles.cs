using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    internal ForeignTitleReply ReadForeignAncestryTitleAck(IReadScope scope, ForeignAncestryProofQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.FromMachine) || query.FromMachine == _machine || string.IsNullOrWhiteSpace(query.ParentSessionId) ||
            query.NodeCount < 1 || !RelayHash.IsSha(query.StructureSha256)) throw TitleRefusal("bad_args");
        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != _machine)
            return new(ForeignTitleReplyKind.Unknown, null, null, null, null);
        var chain = SessionAncestry.Read(scope, _machine, query.ParentSessionId);
        if (!chain.Known) return new(chain.InvalidProof ? ForeignTitleReplyKind.Unknown : ForeignTitleReplyKind.PartsRequired, null, null, null, null);
        var nodes = StoredTitleNodes(scope, chain.Nodes.Reverse().Select(n => n.Id).ToArray());
        if (chain.MarkerResult is null || nodes.Length != query.NodeCount || nodes[^1].Machine != query.FromMachine ||
            ForeignTitleDigests.Structure(_machine, query.FromMachine, query.ParentSessionId, nodes) != query.StructureSha256)
            return new(ForeignTitleReplyKind.Unknown, null, null, null, null);
        return new(ForeignTitleReplyKind.ExistingStructure, null, chain.MarkerResult, query.StructureSha256, ForeignTitleDigests.Stored(_machine, nodes));
    }

    internal ForeignTitleReply ReadForeignRegisterTitleAck(IReadScope scope, ForeignRegisterReceiptQuery query)
    {
        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != _machine)
            return new(ForeignTitleReplyKind.Unknown, null, null, null, null);
        var result = EngineJournal.ReadForeignRegisterReceipt(scope, _machine, query);
        if (result is null) return new(ForeignTitleReplyKind.PartsRequired, null, null, null, null);
        var row = scope.QueryFirst("SELECT machine,state FROM engine_session WHERE id=$id", r => (Machine: r.Text(0), State: r.Text(1)), new SqlArg("$id", query.EntityId));
        var exists = row.Machine == query.FromMachine && row.State == "foreign";
        if (result.Applied && !exists) return new(ForeignTitleReplyKind.Unknown, null, result, null, null);
        var stored = exists ? ForeignTitleDigests.Stored(_machine, StoredTitleNodes(scope, [query.EntityId])) : null;
        return new(ForeignTitleReplyKind.RecordedCanonical, null, result, null, stored);
    }

    private ForeignAncestor[] StoredTitleNodes(IReadScope scope, string[] ids)
    {
        var rows = scope.Query("""
            SELECT id,machine,provider,parent_id,CAST(title AS BLOB) FROM engine_session
            WHERE id IN (SELECT value FROM json_each($ids))
            """, r => new ForeignAncestor(r.Text(0), r.Text(1), r.Text(2), r.IsNull(4) ? throw TitleRefusal("unknown") : RelayHash.Utf8.GetString(r.Blob(4)),
                r.IsNull(3) ? null : r.Text(3)), new SqlArg("$ids", JsonSerializer.Serialize<IReadOnlyList<string>>(ids, EngineJson.Default.IReadOnlyListString)))
            .ToDictionary(n => n.SessionId, StringComparer.Ordinal);
        if (rows.Count != ids.Length) throw TitleRefusal("unknown");
        return ids.Select(id => rows[id]).ToArray();
    }
    private static ForeignAncestryRefused TitleRefusal(string error) => new(new(false, false, error, 0, 0, 0));
}
