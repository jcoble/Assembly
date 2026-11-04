using Assembly.Engine.Sessions;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineStarts
{
    internal static async ValueTask CleanupAsync(IEnumerable<IAsyncDisposable> resources)
    {
        List<Exception> errors = [];
        foreach (var resource in resources)
        {
            try { await resource.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0)
        {
            try { Console.Error.WriteLine($"machine start cleanup failed: {errors.Count} owned resources; {errors[0].GetType().Name}"); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Invocation-owned actual title snapshots and one lease for each creating remote owner.</summary>
    private sealed class StartTitleSources : IAsyncDisposable
    {
        private readonly MachineStarts _owner;
        private readonly McpCaller? _caller;
        private readonly CapturedLiveBatch _batch;
        private readonly ForeignTitleSourceCapture _local;
        private readonly Dictionary<string, RemoteMachine.RelayBulkLease> _leases = new(StringComparer.Ordinal);
        private readonly List<OwnedTitleSource> _remote = [];
        internal SourceSnapshot Source { get; private set; }
        internal CapturedMirror Target { get; }
        internal RemoteMachine.RelayBulkLease TargetLease => _leases[Target.Live.Machine];

        private StartTitleSources(MachineStarts owner, SourceSnapshot source, CapturedLiveBatch batch,
            CapturedMirror target, ForeignTitleSourceCapture local, McpCaller? caller)
            => (_owner, Source, _batch, Target, _local, _caller) = (owner, source, batch, target, local, caller);

        internal static async Task<StartTitleSources> CaptureAsync(MachineStarts owner, SourceSnapshot source,
            string targetMachine, McpCaller? caller, CancellationToken ct)
        {
            var batch = await CapturedLiveBatch.CaptureAsync(owner._connections, owner._runtime.Machine, ct);
            var target = batch.Target(targetMachine);
            target.Remote.RequireMatchingBackendForStart();
            var local = owner._runtime.Reader.ForeignTitleSource(new(ForeignTitleSourceMode.OwnedAncestry,
                source.Parent.Id, targetMachine), ct);
            var capture = new StartTitleSources(owner, source, batch, target, local, caller);
            try
            {
                var groups = source.Structure.GroupBy(node => node.Machine, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                var machines = groups.Keys.Where(machine => machine != owner._runtime.Machine)
                    .Append(targetMachine).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                // Acquiring each distinct owner once also handles an ancestor owned by the target itself.
                foreach (var machine in machines)
                {
                    var actual = batch.Target(machine);
                    capture._leases.Add(machine, await actual.Remote.AcquireRelayBulkAsync(actual.Live.OwnerBinding, ct, actual.Live));
                }
                var nodes = new Dictionary<string, ForeignAncestor>(StringComparer.Ordinal);
                AddOwned(groups[owner._runtime.Machine], local.Nodes, nodes);
                foreach (var (machine, expected) in groups.Where(group => group.Key != owner._runtime.Machine))
                {
                    var lease = capture._leases[machine];
                    var snapshot = await owner._forward.ReadForeignTitlesAsync(lease,
                        new(ForeignTitleSourceMode.OwnedAncestry, expected[^1].Id, targetMachine), ct);
                    capture._remote.Add(new(owner._forward, snapshot, lease));
                    AddOwned(expected, snapshot.Nodes, nodes);
                }
                var chain = source.Structure.Select(node => nodes[node.Id]).ToArray();
                var parent = chain[^1];
                if (parent.Provider != source.Parent.Provider) throw Refused("stale_caller");
                capture.Source = source with { Parent = source.Parent with { Title = parent.Title }, RootToParent = chain };
                await capture.RequireCurrentAsync(ct);
                return capture;
            }
            catch { await capture.DisposeAsync(); throw; }
        }

        private static void AddOwned(IReadOnlyList<AncestryNode> expected, IReadOnlyList<ForeignAncestor> actual,
            Dictionary<string, ForeignAncestor> nodes)
        {
            if (expected.Count != actual.Count) throw Refused("source_changed");
            for (var i = 0; i < expected.Count; i++)
            {
                var before = expected[i]; var node = actual[i];
                if (node.SessionId != before.Id || node.Machine != before.Machine || node.ParentId != before.ParentId ||
                    !nodes.TryAdd(node.SessionId, node)) throw Refused("source_changed");
            }
        }

        internal async Task RequireCurrentAsync(CancellationToken ct)
        {
            _owner._engine.Read(scope => { _owner.RevalidateSource(scope, Source, _caller); return 0; }, ct);
            _owner._runtime.Reader.CheckForeignTitleSource(_local, ct);
            foreach (var source in _remote)
                await _owner._forward.CheckForeignTitlesAsync(source.Lease, source.Snapshot.Opened, ct);
            await _batch.RequireCurrentAsync(ct);
        }

        public ValueTask DisposeAsync() => CleanupAsync(
            _remote.AsEnumerable().Reverse().Cast<IAsyncDisposable>().Concat(_leases.Values.Reverse()));

        private sealed record OwnedTitleSource(MachineForward Forward, ForeignTitleSnapshot Snapshot,
            RemoteMachine.RelayBulkLease Lease) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => new(Forward.CloseForeignTitlesAsync(Lease, Snapshot.Opened.Handle, CancellationToken.None));
        }
    }
}
