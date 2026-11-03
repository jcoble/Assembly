using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Helpers;

/// <summary>Discovers a recipient through a live mirror, then registers its actual owning metadata.</summary>
internal sealed class ForeignStubs(EngineRuntime runtime, RemoteConnections connections, MachineForward forward, TimeProvider time)
{
    private sealed record Owner(RemoteMachine Remote, EngineMirror Mirror, EngineMirror.LiveMirrorBinding Live);

    internal async Task<bool> EnsureAsync(string sessionId, CancellationToken ct)
    {
        var engine = (IEngineRuntime)runtime;
        List<Owner> owners = [];
        HashSet<string> machines = new(StringComparer.Ordinal);
        foreach (var (remote, mirror) in connections.CaptureMirrorOwners())
        {
            if (await mirror.CaptureLiveBindingAsync(ct) is not { } live) continue;
            if (live.Machine == runtime.Machine || !machines.Add(live.Machine)) return false;
            owners.Add(new(remote, mirror, live));
        }
        IReadOnlyList<MachineStartMirrorGeneration> generations = owners.Select(owner =>
            new MachineStartMirrorGeneration(owner.Live.Machine, owner.Live.Generation)).ToArray();
        var eligible = JsonSerializer.Serialize(generations, EngineJson.Default.IReadOnlyListMachineStartMirrorGeneration);
        var machine = engine.Read(scope => Locate(scope, sessionId, eligible), ct);
        if (machine is null) return false;
        var owner = owners.Single(candidate => candidate.Live.Machine == machine);
        await using var lease = await owner.Remote.AcquireRelayBulkAsync(owner.Live.OwnerBinding, ct, owner.Live);
        var source = await forward.ReadForeignTitlesAsync(lease,
            new(ForeignTitleSourceMode.NodeOnly, sessionId, runtime.Machine), ct);
        try
        {
            var node = source.Nodes.Single();
            var prepared = EngineCommands.PrepareSessionTitle(new RegisterForeignSession(machine, node.Provider, node.Title));
            await forward.CheckForeignTitlesAsync(lease, source.Opened, ct);
            if (!await owner.Mirror.IsCurrentLiveBindingAsync(owner.Live, ct)) return false;
            return engine.Write(scope =>
            {
                ct.ThrowIfCancellationRequested();
                if (owner.Live.IsInvalidated || Locate(scope, sessionId, eligible) != machine) return false;
                var current = scope.QueryFirst("SELECT machine,state FROM engine_session WHERE id=$id",
                    r => (Machine: r.Text(0), State: r.Text(1)), new SqlArg("$id", sessionId));
                if (current.Machine is not null) return current.Machine == machine && current.State == "foreign";
                var context = new CommandContext(Guid.NewGuid(), runtime.Machine, new(ActorKind.System, "machine:" + machine),
                    sessionId, 0, 0, time.GetUtcNow().ToUnixTimeMilliseconds());
                return runtime.Commands.ApplyPreparedSessionTitle(scope, context, prepared).Applied;
            });
        }
        finally { await forward.CloseForeignTitlesAsync(lease, source.Opened.Handle, CancellationToken.None); }
    }

    private static string? Locate(IReadScope scope, string session, string eligible)
    {
        var found = scope.Query("""
            SELECT r.machine FROM json_each($owners) o
            JOIN engine_mirror m ON m.machine=json_extract(o.value,'$.machine')
                AND m.generation=json_extract(o.value,'$.generation') AND m.state='live'
            JOIN engine_mirror_row r ON r.machine=m.machine AND r.generation=m.generation
            WHERE r.entity_kind='session' AND r.entity_id=$session
                AND json_extract(r.row_json,'$.machine')=r.machine AND json_extract(r.row_json,'$.state')<>'foreign'
            LIMIT 2
            """, r => r.Text(0), new SqlArg("$owners", eligible), new("$session", session));
        return found.Count == 1 ? found[0] : null;
    }
}
