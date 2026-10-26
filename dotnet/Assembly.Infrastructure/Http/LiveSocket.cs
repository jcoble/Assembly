using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// <c>WS /live?thread=T&amp;after=N</c>: the rows after N, then each row as it's committed, as <c>{type:"row", row}</c>.
/// A quiet socket gets <c>{type:"ping"}</c> every 30 s. A socket that falls behind is closed, and the webview
/// reopens it from the last row it applied.
/// </summary>
internal static class LiveSocket
{
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(30);
    private static readonly byte[] Ping = """{"type":"ping"}"""u8.ToArray();

    public static async Task HandleAsync(HttpContext context, LiveHub hub)
    {
        var threadId = context.Request.Query["thread"].ToString();
        if (!context.WebSockets.IsWebSocketRequest || threadId.Length == 0 || RequestValues.QueryLong(context, "after") is not { } after)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected a WebSocket with ?thread and ?after");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var live = hub.Subscribe(threadId, after);
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var receiving = ReceiveUntilClosedAsync(socket, closing);
        try
        {
            await FollowAsync(socket, live, after, closing.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or LiveFellBehind)
        {
            // The webview went away, or fell behind and will reopen from its watermark.
        }
        finally
        {
            await closing.CancelAsync();
            await receiving;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseQuietlyAsync(socket);
        }
    }

    private static async Task FollowAsync(WebSocket socket, LiveSubscription live, long after, CancellationToken cancellationToken)
    {
        var last = after;
        foreach (var row in live.Backlog)
        {
            await SendAsync(socket, Frame(row), cancellationToken);
            last = row.Seq;
        }

        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(PingEvery);
            try
            {
                if (!await live.Rows.WaitToReadAsync(quiet.Token))
                    return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await SendAsync(socket, Ping, cancellationToken);
                continue;
            }

            while (live.Rows.TryRead(out var row))
            {
                if (row.Seq <= last)
                    continue;
                await SendAsync(socket, Frame(row), cancellationToken);
                last = row.Seq;
            }
        }
    }

    /// <summary>Reads until the webview closes its side, then ends the follow.</summary>
    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource closing)
    {
        var buffer = new byte[1024];
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, closing.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
        }

        await closing.CancelAsync();
    }

    private static ReadOnlyMemory<byte> Frame(StoredRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, RowWire.Options))
        {
            json.WriteStartObject();
            json.WriteString("type", "row");
            json.WritePropertyName("row");
            RowWire.Write(json, row);
            json.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static ValueTask SendAsync(WebSocket socket, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
        }
    }
}
