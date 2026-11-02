using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Mcp;

internal static class McpSessionCallbacks
{
    public static SessionMcp Create(EngineRuntime runtime, McpEndpointReady endpoint, McpTokens tokens)
    {
        var engine = (IEngineRuntime)runtime;
        return new(endpoint.WaitAsync,
            (id, observed) => endpoint.ServersFor(id, engine.Read(scope => ReadOpeningEpoch(scope, runtime.Machine, id, observed), default)),
            tokens.Revoke,
            (id, observed) =>
            {
                engine.Read(scope => ReadOpeningEpoch(scope, runtime.Machine, id, observed), default);
                endpoint.RequireListening();
            });
    }

    private static long ReadOpeningEpoch(IReadScope scope, string machine, string id, SessionObservation? observed)
    {
        if (observed is null || observed.SessionId != id) throw new CallFailed("session_gone");
        var row = scope.QueryFirst("""
            SELECT epoch,state FROM engine_session
            WHERE id=$id AND machine=$machine AND holder='local' AND state<>'foreign'
            """, r => (Epoch: r.Long(0), State: r.Text(1)), new("$id", id), new("$machine", machine));
        if (row.State is null or "ended") throw new CallFailed("session_gone");
        if (row.State == "handing_off") throw new CallFailed("handing_off");
        if (row.Epoch != observed.Epoch) throw new CallFailed("stale_epoch");
        if (row.State is not ("starting" or "idle" or "running" or "needs_me" or "lost")) throw new CallFailed("session_gone");
        return observed.Epoch;
    }
}
