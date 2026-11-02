using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Updates;

/// <summary>Owner-only access to the complete locator snapshot published after the host saves its private capture.</summary>
internal sealed class RemoteCliLocatorCalls(Func<RemoteCliLocator> publishedSnapshot) : IFeature
{
    public void Register(ICallRegistry calls) => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
        RemoteCliLocatorJson.Default.RemoteCliLocator, ReadAsync);

    internal Task<RemoteCliLocator> ReadAsync(JsonElement args, CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("CLI locator unavailable.", "forbidden");
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any())
            throw new CallFailed("CLI locator takes no arguments.", "invalid_args");
        call.Cancellation.ThrowIfCancellationRequested();
        call.Closed.ThrowIfCancellationRequested();
        var locator = publishedSnapshot() ?? throw RemoteCliCapture.Unavailable();
        locator.Validate();
        return Task.FromResult(locator);
    }
}
