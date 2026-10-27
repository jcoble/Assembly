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

    // A close frame or a reset: either way the socket is over.
    internal static async Task AssertEndsAsync(WebSocket socket)
    {
        try
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        }
        catch (WebSocketException)
        {
        }
    }
}
