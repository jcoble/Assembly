using System.Text;
using Microsoft.AspNetCore.Http;
using Assembly.Infrastructure.Http;

namespace Assembly.Infrastructure.Socket;

/// <summary>
/// <c>WS /socket</c>: the one socket the page opens (plan 5.1). The token guard lets it through, since a browser can't
/// set a header on a socket; its <c>hello</c> carries the token instead. <paramref name="machine"/> is the name the
/// <c>welcome</c> gives, this host's.
/// </summary>
internal sealed class SocketEndpoint(string token, SocketDispatcher calls, string machine)
{
    private readonly byte[] _token = Encoding.UTF8.GetBytes(token);
    private readonly CancellationTokenSource _stopping = new();

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected a WebSocket");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        await new SocketConnection(socket, calls).RunAsync(_token, machine, stopping.Token);
    }

    /// <summary>Closes every socket, as the host starts stopping, so it doesn't wait on them.</summary>
    public void CloseAll() => _stopping.Cancel();
}
