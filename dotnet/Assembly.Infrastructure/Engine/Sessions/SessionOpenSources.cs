using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Engine.Sessions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResolveSessionSourceArgs([property: JsonRequired] string? RouteMachine);

/// <summary>
/// Route discovery, never authority for an old target's identity or history. A machine name resolves to its one mirror
/// here in any state but retired, connected or not (TSK-1692): each call that needs the remote checks it's live.
/// </summary>
internal sealed class SessionOpenSources(IAppDatabase db, EngineRuntime runtime)
{
    internal Task<ExpectedSource> ResolveAsync(ResolveSessionSourceArgs args, CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        call.Closed.ThrowIfCancellationRequested();
        if (args.RouteMachine is null) return Task.FromResult(new ExpectedSource(runtime.Machine, 0, null));
        if (string.IsNullOrWhiteSpace(args.RouteMachine)) throw Changed();
        ExpectedSource expected;
        using (var snapshot = db.OpenSnapshot(call.Cancellation))
        {
            var candidates = Mirrors(snapshot, args.RouteMachine);
            if (candidates.Count != 1 || candidates[0].Machine == runtime.Machine) throw Changed();
            expected = candidates[0];
        }
        var guard = new CurrentSource(() =>
        {
            if (db.QueryFirst("SELECT COUNT(*) FROM engine_mirror WHERE name=$route AND state<>'retired'", row => row.Long(0), default,
                    new SqlArg("$route", args.RouteMachine)) != 1 ||
                db.QueryFirst("SELECT EXISTS(SELECT 1 FROM engine_mirror WHERE machine=$machine AND name=$route AND state<>'retired')",
                    row => row.Long(0), default, new SqlArg("$machine", expected.Machine), new("$route", args.RouteMachine)) != 1)
                throw Changed();
        });
        call.SourceGuard = guard;
        guard.EnsureCurrent();
        return Task.FromResult(expected);
    }

    // The route's mirrors in any state but retired; more than one is ambiguous.
    private static IReadOnlyList<ExpectedSource> Mirrors(IReadScope scope, string route) => scope.Query("""
        SELECT machine,generation,name FROM engine_mirror
        WHERE name=$route AND state<>'retired' ORDER BY machine LIMIT 2
        """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), new SqlArg("$route", route));

    private sealed class CurrentSource(Action ensureCurrent) : IExpectedSourceGuard
    {
        private int _disposed;
        public CancellationToken Changed => CancellationToken.None;
        public void EnsureCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0) throw SessionOpenSources.Changed();
            ensureCurrent();
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
    private static CallFailed Changed() => new("The selected session source changed.", "source_changed");
}
