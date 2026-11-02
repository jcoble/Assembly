using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static CommandResult ApplyForeignAncestryBatch(IWriteScope scope, string target, PreparedForeignAncestryBatch prepared) =>
        ForeignParentAdmission.ApplyBatch(scope, target, prepared);

    internal static CommandContext ForeignParentContext(string target, ForeignAncestor node) =>
        new(RelayReceiptKeys.ContextGuid(RelayReceiptKeys.ForeignParent(target, node.SessionId)), target,
            new(ActorKind.System, "machine:" + node.Machine), node.SessionId, 0, 0, 0);

    private static string ForeignParentCanonical(string? parent) =>
        JsonSerializer.Serialize<EngineCommand>(new EstablishForeignParent(parent), EngineJson.Default.EngineCommand);

    internal static bool IsForeignParentProof(string target, ForeignAncestor node, string fingerprint, string resultJson)
    {
        if (fingerprint != Fingerprint(ForeignParentContext(target, node), ForeignParentCanonical(node.ParentId))) return false;
        try
        {
            var result = JsonSerializer.Deserialize(resultJson, EngineJson.Default.CommandResult);
            return result is { Applied: true, Late: false, Error: null, Version: 0, Seq: 0, Epoch: 0 } &&
                JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) == resultJson;
        }
        catch (JsonException) { return false; }
    }

    private static CommandResult ExecuteForeignParent(ForeignParentAdmission admission) =>
        ExecuteCore(admission.Scope, admission.Context, admission.Key, admission.Canonical, admission.Transition);

    // Nonwire admission can only be constructed after whole-chain validation in this exact supplied scope.
    private sealed class ForeignParentAdmission
    {
        internal IWriteScope Scope { get; }
        internal CommandContext Context { get; }
        internal string Key { get; }
        internal string Canonical { get; }
        private readonly IReadOnlyList<ForeignAncestor> _nodes;
        private readonly PreparedForeignAncestryBatch _prepared;
        private readonly int _index;
        private readonly string _target;
        private readonly string _chainDigest;
        private readonly HashSet<string> _accepted;

        private ForeignParentAdmission(IWriteScope scope, string target, PreparedForeignAncestryBatch prepared, int index, HashSet<string> accepted)
        {
            Scope = scope; _target = target; _prepared = prepared; _nodes = prepared.Nodes; _index = index; _chainDigest = prepared.ChainDigest;
            var nodes = _nodes;
            _accepted = accepted;
            Context = ForeignParentContext(target, nodes[index]);
            Key = RelayReceiptKeys.ForeignParent(target, nodes[index].SessionId);
            Canonical = ForeignParentCanonical(nodes[index].ParentId);
        }

        internal static CommandResult ApplyBatch(IWriteScope scope, string target, PreparedForeignAncestryBatch prepared)
        {
            if (prepared.TargetMachine != target || scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != target)
                throw Refusal("wrong_machine");
            var nodes = prepared.Nodes;
            if (string.IsNullOrWhiteSpace(prepared.FromMachine) || prepared.FromMachine == target || nodes is not { Count: > 0 } ||
                !Plain(prepared.ParentSessionId))
                throw Refusal("bad_args");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node is null || !Plain(node.SessionId) || !ids.Add(node.SessionId) || string.IsNullOrWhiteSpace(node.Machine) ||
                    string.IsNullOrWhiteSpace(node.Provider) || node.Title is null || node.ParentId != (i == 0 ? null : nodes[i - 1].SessionId))
                    throw Refusal("bad_args");
            }
            if (nodes[^1].SessionId != prepared.ParentSessionId || nodes[^1].Machine != prepared.FromMachine)
                throw Refusal("forbidden");

            // Read all existing targets once, before any reference, FK or private receipt is written.
            var parameters = nodes.Select((n, i) => new SqlArg("$n" + i, n.SessionId)).ToArray();
            var existing = scope.Query($"SELECT id,machine,state,parent_id,version,epoch,holder,managed_checkout FROM engine_session WHERE id IN ({string.Join(',', parameters.Select(p => p.Name))})",
                r => new Existing(r.Text(0), r.Text(1), r.Text(2), r.IsNull(3) ? null : r.Text(3), r.Long(4), r.Long(5), r.Text(6), r.Long(7)), parameters)
                .ToDictionary(n => n.Id, StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (!existing.TryGetValue(node.SessionId, out var row))
                {
                    if (node.Machine == target) throw Refusal("forbidden");
                    continue;
                }
                if (row.Machine != node.Machine) throw Refusal("forbidden");
                if (node.Machine == target)
                {
                    if (row.State == "foreign" || row.Parent != node.ParentId) throw Refusal("forbidden");
                }
                else if (row.State != "foreign" || row.Version != 0 || row.Epoch != 0 || row.Holder != node.Machine || row.Managed != 0 ||
                    row.Parent is not null && row.Parent != node.ParentId) throw Refusal("forbidden");
            }
            var foreign = nodes.Where(n => n.Machine != target).ToArray();
            var markerArgs = foreign.Select((n, i) => new SqlArg("$marker" + i, RelayReceiptKeys.ForeignParent(target, n.SessionId))).ToArray();
            var markers = scope.Query($"SELECT command_id,fingerprint,result_json FROM engine_command_receipt WHERE command_id IN ({string.Join(',', markerArgs.Select(p => p.Name))})",
                r => (Key: r.Text(0), Hash: r.Text(1), Result: r.Text(2)), markerArgs).ToDictionary(r => r.Key, StringComparer.Ordinal);
            foreach (var node in foreign)
            {
                if (!markers.TryGetValue(RelayReceiptKeys.ForeignParent(target, node.SessionId), out var receipt))
                {
                    // An unexplained persisted link can never be blessed by a new marker.
                    if (existing.TryGetValue(node.SessionId, out var unexplained) && unexplained.Parent is not null) throw Refusal("bad_args");
                    continue;
                }
                if (!IsForeignParentProof(target, node, receipt.Hash, receipt.Result)) throw Refusal("command_id_conflict");
                if (!existing.TryGetValue(node.SessionId, out var actual) || actual.Parent != node.ParentId) throw Refusal("bad_args");
            }
            HashSet<string> accepted = new(StringComparer.Ordinal);
            foreach (var (node, i) in nodes.Select((node, i) => (node, i)))
            {
                if (node.Machine == target) { accepted.Add(node.SessionId); continue; }
                var admitted = new ForeignParentAdmission(scope, target, prepared, i, accepted);
                var result = ExecuteForeignParent(admitted);
                if (!result.Applied) throw new ForeignAncestryRefused(result);
                accepted.Add(node.SessionId);
            }
            // One shared recursive proof read after the whole batch, never a chain read for each node.
            if (!SessionAncestry.Read(scope, target, prepared.ParentSessionId).Known) throw Refusal("bad_args");
            return new(true, false, null, 0, 0, 0);
        }

        internal CommandResult Transition()
        {
            var node = _nodes[_index];
            if (_prepared.TargetMachine != _target || _chainDigest != _prepared.ChainDigest || !ReferenceEquals(_nodes, _prepared.Nodes) ||
                Context != ForeignParentContext(_target, node) || Key != RelayReceiptKeys.ForeignParent(_target, node.SessionId) || Canonical != ForeignParentCanonical(node.ParentId))
                throw Refusal("forbidden");
            if (_index == 0 ? node.ParentId is not null :
                node.ParentId != _nodes[_index - 1].SessionId || !_accepted.Contains(_nodes[_index - 1].SessionId))
                throw Refusal("bad_args");
            var registration = new RegisterForeignSession(node.Machine, node.Provider, node.Title);
            var result = SessionTransitions.ApplyPreparedTitle(Scope, Context, registration, 1, 1, "", _prepared.TitleUtf8(_index));
            if (!result.Applied) return result;
            var row = Scope.QueryFirst("SELECT machine,state,parent_id,version FROM engine_session WHERE id=$id",
                r => (Machine: r.Text(0), State: r.Text(1), Parent: r.IsNull(2) ? null : r.Text(2), Version: r.Long(3)), new SqlArg("$id", node.SessionId));
            if (row.Machine != node.Machine || row.State != "foreign" || row.Version != 0 || row.Parent is not null && row.Parent != node.ParentId)
                throw Refusal("forbidden");
            Scope.Execute("UPDATE engine_session SET parent_id=$parent WHERE id=$id", new("$parent", node.ParentId), new("$id", node.SessionId));
            return new(true, false, null, 0, 0, 0);
        }

        private static bool Plain(string? id) => !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") && !id.Contains('/') && !id.Contains('\\') && !id.Any(char.IsControl);
        private sealed record Existing(string Id, string Machine, string State, string? Parent, long Version, long Epoch, string Holder, long Managed);
    }

    private static ForeignAncestryRefused Refusal(string error) => new(new(false, false, error, 0, 0, 0));
}
