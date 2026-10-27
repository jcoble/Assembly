using System.Net.WebSockets;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // The debug control's drop (diagnostics/drop): every open socket closes, and the window reconnects.
    [Fact]
    public async Task Drop_closes_every_open_socket()
    {
        using var first = await OpenAsync();
        using var second = await OpenAsync();

        _endpoint.DropAll();

        foreach (var socket in new[] { first, second })
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        }
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
}
