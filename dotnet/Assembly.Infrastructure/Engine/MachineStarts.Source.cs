using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine.Mcp;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineStarts
{
    private sealed record ParentSource(string Id, long Epoch, string State, string? ParentId,
        string Provider, string? Model, string Folder, string Title, bool Managed, string? Repository);
    private sealed record SourceSnapshot(ParentSource Parent, IReadOnlyList<AncestryNode> Structure, IReadOnlyList<ForeignAncestor> RootToParent);

    private SourceSnapshot ReadSource(IReadScope scope, string parentId, McpCaller? caller)
    {
        if (caller is not null)
        {
            if (caller.SessionId != parentId) throw Refused("forbidden");
            if (_gate.Resolve(scope, caller) is null) throw Refused("session_gone");
        }
        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != _runtime.Machine)
            throw Refused("wrong_machine");
        var parent = scope.QueryFirst("""
            SELECT e.id,e.epoch,e.state,e.parent_id,e.provider,e.model,e.folder,CAST(e.title AS BLOB),
                   e.managed_checkout,c.repo_url,e.reserved_epoch
            FROM engine_session e LEFT JOIN session_info c ON c.session_id=e.id
            WHERE e.id=$id AND e.machine=$machine AND e.holder='local' AND e.state<>'foreign'
            """, row => (Parent: new ParentSource(row.Text(0), row.Long(1), row.Text(2), row.IsNull(3) ? null : row.Text(3),
                row.Text(4), row.IsNull(5) ? null : row.Text(5), row.Text(6), Utf8.GetString(row.Blob(7)), row.Long(8) == 1,
                row.IsNull(9) ? null : row.Text(9)), Reserved: row.IsNull(10) ? (long?)null : row.Long(10)),
            new SqlArg("$id", parentId), new SqlArg("$machine", _runtime.Machine));
        if (parent.Parent is null) throw Refused(caller is null ? "forbidden" : "session_gone");
        if (parent.Parent.State is "ended" or "lost") throw Refused(caller is null ? "invalid_transition" : "session_gone");
        if (parent.Parent.State == "handing_off" || parent.Reserved is not null) throw Refused("handing_off");
        var ancestry = SessionAncestry.Read(scope, _runtime.Machine, parentId);
        if (!ancestry.Known) throw Refused("bad_args");
        // Structural proofs establish links only. Actual owning snapshots supply metadata later.
        return new(parent.Parent, ancestry.Nodes.Reverse().ToArray(), []);
    }

    private void RevalidateSource(IReadScope scope, SourceSnapshot source, McpCaller? caller)
    {
        // Revalidate ownership and intent metadata, not the frozen display-title snapshots.
        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != _runtime.Machine)
            throw Refused("wrong_machine");
        var current = scope.QueryFirst("""
            SELECT e.epoch,e.state,e.parent_id,e.provider,e.model,e.folder,e.managed_checkout,c.repo_url,e.reserved_epoch
            FROM engine_session e LEFT JOIN session_info c ON c.session_id=e.id
            WHERE e.id=$id AND e.machine=$machine AND e.holder='local' AND e.state<>'foreign'
            """, row => new ParentFence(row.Long(0), row.Text(1), row.IsNull(2) ? null : row.Text(2), row.Text(3),
                row.IsNull(4) ? null : row.Text(4), row.Text(5), row.Long(6) == 1, row.IsNull(7) ? null : row.Text(7),
                row.IsNull(8) ? null : row.Long(8)), new SqlArg("$id", source.Parent.Id), new SqlArg("$machine", _runtime.Machine));
        if (current is null || current.State is "ended" or "lost") throw Refused(caller is null ? "invalid_transition" : "session_gone");
        if (caller is not null && current.Epoch != caller.Epoch) throw Refused("stale_epoch");
        if (current.State == "handing_off" || current.ReservedEpoch is not null) throw Refused("handing_off");
        var parent = source.Parent;
        if (current.Epoch != parent.Epoch || current.ParentId != parent.ParentId || current.Provider != parent.Provider ||
            current.Model != parent.Model || current.Folder != parent.Folder || current.Managed != parent.Managed || current.Repository != parent.Repository)
            throw Refused("stale_caller");
        var ancestry = SessionAncestry.Read(scope, _runtime.Machine, parent.Id);
        var nodes = ancestry.Nodes.Reverse().ToArray();
        if (!ancestry.Known || nodes.Length != source.Structure.Count ||
            nodes.Where((node, index) => node.Id != source.Structure[index].Id || node.Machine != source.Structure[index].Machine ||
                node.ParentId != source.Structure[index].ParentId).Any()) throw Refused("stale_caller");
        var owned = source.RootToParent.Where(node => node.Machine == _runtime.Machine).ToArray();
        var parameters = owned.Select((node, index) => new SqlArg("$node" + index, node.SessionId)).ToArray();
        var providers = scope.Query($"SELECT id,provider FROM engine_session WHERE machine=$machine AND holder='local' AND state<>'foreign' AND id IN ({string.Join(',', parameters.Select(parameter => parameter.Name))})",
            row => (Id: row.Text(0), Provider: row.Text(1)), [.. parameters, new SqlArg("$machine", _runtime.Machine)])
            .ToDictionary(row => row.Id, row => row.Provider, StringComparer.Ordinal);
        if (providers.Count != owned.Length || owned.Any(node => providers[node.SessionId] != node.Provider)) throw Refused("stale_caller");

    }

    private sealed record ParentFence(long Epoch, string State, string? ParentId, string Provider, string? Model,
        string Folder, bool Managed, string? Repository, long? ReservedEpoch);

    private void FinalSourceCheck(SourceSnapshot source, McpCaller? caller, CapturedLiveBatch batch)
    {
        _engine.Write(scope =>
        {
            RevalidateSource(scope, source, caller);
            var error = MachineStartCheck.Check(scope, source.Parent.Id, _limits.ReadDepth(), _limits.ReadChildren(),
                batch.Generations, out _);
            if (error is not null) throw Refused(error);
            return 0;
        });
    }

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static McpRefused Refused(string error) => new(new(error));
}
