using System.Text.Json;
using System.Net.WebSockets;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    internal async Task<JsonElement> CallOwnerBindingAsync(OwnerConnectionBinding binding, string method, JsonElement args,
        CancellationToken ct, EngineMirror.LiveMirrorBinding? live = null, ClientWebSocket? expectedConnection = null, FrozenMachineStartIntent? startIntent = null)
    {
        if (binding.IsInvalidated) throw new CallFailed("owner_changed");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Invalidated, _socketStop.Token);
        return await CallAsync("engine", method, args, false, linked.Token, binding.Machine,
            expectedConnection: expectedConnection, expectedOwner: binding, expectedMirror: live, startIntent: startIntent).ConfigureAwait(false);
    }

    // Atomic flags only: never enters the mirror gate while holding the send gate.
    private static void RequireLiveMirror(OwnerConnectionBinding binding, EngineMirror.LiveMirrorBinding? live)
    {
        if (live is not null && (live.IsInvalidated || !ReferenceEquals(live.OwnerBinding, binding)))
            throw new CallFailed("mirror_changed");
    }
}
