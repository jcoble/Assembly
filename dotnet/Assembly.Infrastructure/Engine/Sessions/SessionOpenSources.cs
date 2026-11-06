using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Sessions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResolveSessionSourceArgs([property: JsonRequired] string? RouteMachine);

/// <summary>Current online route discovery, never authority for an old target's identity or history.</summary>
internal sealed class SessionOpenSources(IAppDatabase db, EngineRuntime runtime, RemoteConnections? connections)
{
    internal async Task<ExpectedSource> ResolveAsync(ResolveSessionSourceArgs args, CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        call.Closed.ThrowIfCancellationRequested();
        if (args.RouteMachine is null) return new(runtime.Machine, 0, null);
        if (string.IsNullOrWhiteSpace(args.RouteMachine) || connections is null) throw Changed();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed);
        ExpectedSource expected;
        using (var snapshot = db.OpenSnapshot(stop.Token))
        {
            var candidates = snapshot.Query("""
                SELECT machine,generation,name FROM engine_mirror
                WHERE name=$route AND state<>'retired' ORDER BY machine LIMIT 2
                """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), new SqlArg("$route", args.RouteMachine));
            if (candidates.Count != 1 || candidates[0].Machine == runtime.Machine) throw Changed();
            expected = candidates[0];
        }
        var captured = await connections.CaptureSessionOpenSourceAsync(db, expected, stop.Token);
        if (captured.Live.Machine != expected.Machine || captured.Live.Generation != expected.Generation ||
            captured.Live.OwnerBinding.Machine != expected.Machine || expected.RouteMachine != args.RouteMachine) throw Changed();
        var guard = new CurrentSource(captured.Live.Invalidated, captured.EnsureCurrent);
        call.SourceGuard = guard;
        guard.EnsureCurrent();
        return expected;
    }

    private sealed class CurrentSource(CancellationToken changed, Action ensureCurrent) : IExpectedSourceGuard
    {
        private int _disposed;
        public CancellationToken Changed => changed;
        public void EnsureCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0 || changed.IsCancellationRequested) throw SessionOpenSources.Changed();
            ensureCurrent();
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
    private static CallFailed Changed() => new("The selected session source changed.", "source_changed");
}
