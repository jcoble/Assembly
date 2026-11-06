using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private sealed record GitSubscription(ClientWebSocket Socket, Channel<JsonElement> Frames)
    {
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<long, GitSubscription> _gitSubscriptions = [];

    internal async IAsyncEnumerable<JsonElement> GitOperationFramesAsync(Guid operationId, string expectedEngineId,
        ClientWebSocket expectedConnection, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextSub);
        var frames = Channel.CreateBounded<JsonElement>(16);
        var subscription = new GitSubscription(expectedConnection, frames);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _socketStop.Token);
        await _socketSend.WaitAsync(linked.Token);
        try
        {
            var socket = RequireEngine(expectedEngineId);
            if (!ReferenceEquals(socket, expectedConnection))
                throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed before subscription");
            _gitSubscriptions.Add(id, subscription);
            var key = expectedEngineId + "/" + operationId.ToString("D");
            await SendFrameAsync(socket, $"{{\"t\":\"sub\",\"id\":{id},\"topic\":\"gitOperation\",\"key\":{JsonSerializer.Serialize(key, SessionsJson.Default.String)}}}", linked.Token);
        }
        catch { _gitSubscriptions.Remove(id); throw; }
        finally { _socketSend.Release(); }
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(linked.Token)) yield return frame;
        }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _gitSubscriptions.Remove(id); frames.Writer.TryComplete();
                if (ReferenceEquals(_socket, expectedConnection) && _socket is { State: WebSocketState.Open } socket)
                    try { await SendFrameAsync(socket, $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); subscription.Closed.TrySetResult(); }
        }
    }

    // Called only under the existing socket gate; subscriptions are never moved to a replacement socket.
    private void CloseGitSubscriptions(ClientWebSocket? socket)
    {
        foreach (var subscription in _gitSubscriptions.Values)
            if (socket is null || ReferenceEquals(subscription.Socket, socket))
                subscription.Frames.Writer.TryComplete(new MachineUnreachable(_name, $"{_name}: Git operation connection closed"));
    }
}
