using System.Net.WebSockets;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // The debug control's drop (diagnostics/drop): every open socket ends, as a lost connection does (the backend's
    // cancelled receive aborts it, with no close frame), and the window reconnects.
    [Fact]
    public async Task Drop_ends_every_open_socket()
    {
        using var first = await OpenAsync();
        using var second = await OpenAsync();

        _endpoint.DropAll();

        await AssertEndsAsync(first);
        await AssertEndsAsync(second);
    }

    // A drop isn't the host stopping: the socket the window opens next works.
    [Fact]
    public async Task A_socket_opened_after_a_drop_works()
    {
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var dropped = await OpenAsync();
        _endpoint.DropAll();

        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "notes", "echo", """{"text":"after"}""");

        Assert.Equal("""{"t":"result","id":1,"result":{"text":"after"}}""", await ReadAsync(socket));
    }

    // A drop while a sub's state is loading on the receive loop cancels the load, rather than waiting for it.
    [Fact]
    public async Task Drop_cancels_a_subs_state_while_it_loads()
    {
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls.Topic("notes", TestJson.Default.Note, note => note.Text, (_, call) =>
        {
            loading.SetResult();
            cancelled.SetResult(call.Cancellation.WaitHandle.WaitOne(Wait));
            call.Cancellation.ThrowIfCancellationRequested();
            return [];
        });
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"notes"}""");
        await loading.Task.WaitAsync(Wait);

        _endpoint.DropAll();

        Assert.True(await cancelled.Task.WaitAsync(Wait));
        await AssertEndsAsync(socket);
    }

    // A close frame or a reset, after any frames already on their way: either way the socket is over.
    internal static async Task AssertEndsAsync(WebSocket socket)
    {
        using var limit = new CancellationTokenSource(Wait);
        try
        {
            while ((await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64 * 1024]), limit.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
        }
    }
}
