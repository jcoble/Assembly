using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    internal async Task<(string Identity, System.Net.WebSockets.ClientWebSocket Socket)> CaptureOwnerReadAsync(
        string? expectedEngineId, CancellationToken cancellationToken)
    {
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            var identity = _engineIdentity ?? throw Unreachable("unverified engine peer", $"{_name}: engine identity is unverified");
            var socket = RequireEngine(identity);
            if (expectedEngineId is not null && expectedEngineId != identity)
                throw Unreachable("unverified engine peer", $"{_name}: owning engine identity differs");
            return (identity, socket);
        }
        finally { _socketSend.Release(); }
    }

    internal async Task<JsonElement> ReadOwnerPromptAsync(string method, JsonElement args, bool bulk,
        string? expectedEngineId, CancellationToken cancellationToken)
    {
        var owner = await CaptureOwnerReadAsync(expectedEngineId, cancellationToken);
        return await ReadOwnerOnAsync(method, args, bulk, owner, cancellationToken);
    }

    internal async Task<OwnerPromptPage> ReadOwnerPromptPageAsync(OwnerPromptReadArgs args,
        Func<OwnerPromptPage, int> encodedBytes, string? expectedEngineId, CancellationToken cancellationToken)
    {
        var owner = await CaptureOwnerReadAsync(expectedEngineId, cancellationToken);
        async Task<OwnerPromptPage> Read(OwnerPromptReadArgs request) =>
            (await ReadOwnerOnAsync("owner_prompts", JsonSerializer.SerializeToElement(request with { Machine = null },
                SessionsJson.Default.OwnerPromptReadArgs), false, owner, cancellationToken)).Deserialize(SessionsJson.Default.OwnerPromptPage)!;
        var page = await Read(args);
        if (encodedBytes(page) > 64 * 1024)
        {
            if (page.Items.Count < 2) throw new Assembly.Application.Common.Calls.CallFailed("owner response exceeds budget");
            page = await Read(args with { Limit = page.Items.Count - 1 });
            if (encodedBytes(page) > 64 * 1024) throw new Assembly.Application.Common.Calls.CallFailed("owner response exceeds budget");
        }
        // Measurement can run caller code; retain the original target through the final return too.
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(RequireEngine(owner.Identity), owner.Socket))
                throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed");
        }
        finally { _socketSend.Release(); }
        return page;
    }

    private async Task<JsonElement> ReadOwnerOnAsync(string method, JsonElement args, bool bulk,
        (string Identity, System.Net.WebSockets.ClientWebSocket Socket) owner, CancellationToken cancellationToken, OwnerConnectionBinding? ownerBinding = null)
    {
        try { return await CallAsync("engine", method, args, bulk, cancellationToken, owner.Identity, owner.Socket, expectedOwner: ownerBinding); }
        catch (Exception error) when (error is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed", error); }
    }

    internal async Task<OwnerPromptAccepted> SendOwnerPromptAsync(OwnerPromptRequest request, CancellationToken cancellationToken)
    {
        var result = await CallAsync("send", "send", new SendFeature.SendArgs(request.Session, request.Text,
            request.ClientId, request.AttachmentIds, request.Epoch), cancellationToken);
        var accepted = result.Deserialize(SessionsJson.Default.OwnerPromptAccepted)
            ?? throw new IOException("Invalid owner prompt acknowledgement");
        _follower.Watch(request.Session);
        return accepted;
    }
}
