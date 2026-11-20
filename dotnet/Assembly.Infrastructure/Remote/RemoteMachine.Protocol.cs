using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// A remote on another socket protocol (TSK-1692). One on an older protocol closes a newer hello with 4400 "expected a
/// hello", which from protocol 2 on also names its number; that remote is unreachable with the reason, and an older one is
/// offered its update.
/// </summary>
internal sealed partial class RemoteMachine
{
    private const string HelloRefused = "expected a hello";

    private async Task<JsonElement> ReadHelloAnswerAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try { return await ReadFrameAsync(socket, cancellationToken); }
        catch (IOException) when (socket.CloseStatus == (WebSocketCloseStatus)4400 && RefusingProtocol(socket.CloseStatusDescription) is { } protocol)
        {
            throw ProtocolRefused(protocol);
        }
    }

    // Protocol 1 says only "expected a hello"; later ones add " with protocol {n}".
    internal static int? RefusingProtocol(string? reason) =>
        reason == HelloRefused ? 1
        : reason is not null && reason.StartsWith(HelloRefused + " with protocol ", StringComparison.Ordinal)
          && int.TryParse(reason.AsSpan(HelloRefused.Length + " with protocol ".Length), out var protocol) ? protocol
        : null;

    private MachineUnreachable ProtocolRefused(int protocol)
    {
        var reason = $"runs protocol {protocol}; this computer needs {Socket.SocketConnection.Protocol}";
        var error = Unreachable(reason, $"{_name} {reason}");
        if (protocol < Socket.SocketConnection.Protocol) _status.ProtocolOlder(_name);
        return error;
    }
}
