using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task<AcceptedAncestry> CaptureAcceptedAncestryAsync(string sessionId,
        SessionsFeature.CreatedSessionFacts facts, OwnerConnectionBinding owner,
        EngineMirror.LiveMirrorBinding? live, CancellationToken ct)
    {
        var engine = RequireAcceptanceEngine();
        if (engine.Machine == owner.Machine) throw AcceptedSourceChanged();
        var forward = new MachineForward(machine => machine == owner.Machine ? this : null);
        var lease = await AcquireRelayBulkAsync(owner, ct, live);
        ForeignTitleSnapshot? snapshot = null;
        try
        {
            snapshot = await forward.ReadForeignTitlesAsync(lease,
                new(ForeignTitleSourceMode.OwnedAncestry, sessionId, engine.Machine), ct);
            var nodes = snapshot.Nodes;
            var anchor = nodes[^1];
            if (anchor.SessionId != sessionId || anchor.Machine != facts.Machine ||
                anchor.Provider != facts.Agent || anchor.ParentId != facts.ParentSessionId)
                throw AcceptedSourceChanged();
            IReadOnlyList<ForeignAncestor> prefix = [];
            if (nodes[0].ParentId is { } parent)
                prefix = ((IEngineRuntime)engine).Read(scope => engine.Commands.ReadKnownAncestry(scope, parent), ct)
                    ?? throw AcceptedSourceChanged();
            var chain = prefix.Concat(nodes).ToArray();
            var prepared = engine.Commands.PrepareForeignAncestry(new(owner.Machine, sessionId, chain));
            await forward.CheckForeignTitlesAsync(lease, snapshot.Opened, ct);
            return new(engine, forward, lease, snapshot, prefix, prepared, ct);
        }
        catch (Exception error)
        {
            try
            {
                if (snapshot is not null)
                    await forward.CloseForeignTitlesAsync(lease, snapshot.Opened.Handle, CancellationToken.None);
            }
            finally { await lease.DisposeAsync(); }
            if (error is ForeignAncestryRefused) throw AcceptedSourceChanged();
            throw;
        }
    }

    private sealed class AcceptedAncestry(EngineRuntime engine, MachineForward forward, RelayBulkLease lease,
        ForeignTitleSnapshot snapshot, IReadOnlyList<ForeignAncestor> prefix,
        PreparedForeignAncestryBatch prepared, CancellationToken cancellation) : IAsyncDisposable
    {
        internal void Apply(IWriteScope scope)
        {
            cancellation.ThrowIfCancellationRequested();
            if (prefix.Count > 0)
            {
                var current = engine.Commands.ReadKnownAncestry(scope, prefix[^1].SessionId);
                if (current is null || !prefix.SequenceEqual(current)) throw AcceptedSourceChanged();
            }
            // Existing structural receipts do not certify a new provider assertion on replay.
            var providers = scope.Query("""
                SELECT id,provider FROM engine_session WHERE id IN (SELECT value FROM json_each($ids))
                """, row => (Id: row.Text(0), Provider: row.Text(1)), new SqlArg("$ids",
                JsonSerializer.Serialize<IReadOnlyList<string>>(snapshot.Nodes.Select(n => n.SessionId).ToArray(), EngineJson.Default.IReadOnlyListString)))
                .ToDictionary(row => row.Id, row => row.Provider, StringComparer.Ordinal);
            if (snapshot.Nodes.Any(node => providers.TryGetValue(node.SessionId, out var actual) && actual != node.Provider))
                throw AcceptedSourceChanged();
            try
            {
                var applied = engine.Commands.ApplyForeignAncestryBatch(scope, prepared);
                if (!applied.Applied) throw AcceptedSourceChanged();
            }
            catch (ForeignAncestryRefused) { throw AcceptedSourceChanged(); }
            cancellation.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            try { await forward.CloseForeignTitlesAsync(lease, snapshot.Opened.Handle, CancellationToken.None); }
            finally { await lease.DisposeAsync(); }
        }
    }

    private EngineRuntime RequireAcceptanceEngine() => _acceptanceEngine
        ?? throw new CallFailed("Accepted-session ownership verification is unavailable.", "configuration_unavailable");

    private static CallFailed AcceptedSourceChanged() =>
        new("The accepted session's actual owner or certified ancestry changed.", "source_changed");
}
