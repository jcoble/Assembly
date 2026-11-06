using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private sealed record RuntimeSubscription(OwnerConnectionBinding Owner, Channel<JsonElement> Frames)
    {
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<long, RuntimeSubscription> _runtimeSubscriptions = [];

    internal async IAsyncEnumerable<ProviderRuntimeChanged> ProviderRuntimeFramesAsync(EngineMirror.LiveMirrorBinding live,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextSub);
        var subscription = new RuntimeSubscription(live.OwnerBinding, Channel.CreateBounded<JsonElement>(16));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _socketStop.Token, live.Invalidated);
        await _socketSend.WaitAsync(linked.Token);
        try
        {
            var socket = RequireOwnerBinding(live.OwnerBinding);
            RequireLiveMirror(live.OwnerBinding, live);
            _runtimeSubscriptions.Add(id, subscription);
            var key = JsonSerializer.Serialize(live.Machine, Assembly.Application.Features.Chat.Sessions.SessionsJson.Default.String);
            await SendFrameAsync(socket, $"{{\"t\":\"sub\",\"id\":{id},\"topic\":\"provider_runtime\",\"key\":{key}}}", linked.Token);
        }
        catch { _runtimeSubscriptions.Remove(id); throw; }
        finally { _socketSend.Release(); }
        try
        {
            await foreach (var frame in subscription.Frames.Reader.ReadAllAsync(linked.Token))
            {
                var type = frame.GetProperty("t").GetString();
                if (type == "error") throw new CallFailed("The owning settings subscription was refused.",
                    frame.TryGetProperty("code", out var code) ? code.GetString() : "failed");
                if (type == "state")
                {
                    var items = frame.GetProperty("items");
                    if (items.GetArrayLength() != 1) throw new IOException("Invalid owning settings state.");
                    foreach (var item in items.EnumerateArray()) yield return RuntimeChange(item, live.Machine);
                }
                else if (type == "change" && frame.GetProperty("op").GetString() == "upsert") yield return RuntimeChange(frame, live.Machine);
                else throw new IOException("Invalid owning settings frame.");
            }
        }
        finally
        {
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _runtimeSubscriptions.Remove(id);
                subscription.Frames.Writer.TryComplete();
                if (!live.OwnerBinding.IsInvalidated && !_socketStop.IsCancellationRequested)
                    try { await SendFrameAsync(RequireOwnerBinding(live.OwnerBinding), $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is MachineUnreachable or WebSocketException or OperationCanceledException) { }
            }
            finally { _socketSend.Release(); subscription.Closed.TrySetResult(); }
        }
    }

    private static ProviderRuntimeChanged RuntimeChange(JsonElement frame, string machine)
    {
        var item = frame.GetProperty("item").Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeChanged);
        if (frame.GetProperty("key").GetString() != machine || item is null || item.Machine != machine ||
            item.Revision is not { Length: 64 } || item.Revision.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("Invalid owning settings revision.");
        return item;
    }

    // The existing socket receive gate owns this dictionary. A stream never moves to a replacement connection.
    private bool ReceiveProviderRuntime(JsonElement frame, ClientWebSocket socket)
    {
        if (!(frame.TryGetProperty("sub", out var id) || frame.TryGetProperty("id", out id)) ||
            !_runtimeSubscriptions.TryGetValue(id.GetInt64(), out var subscription)) return false;
        if (subscription.Owner.IsInvalidated || !ReferenceEquals(RequireOwnerBinding(subscription.Owner), socket))
            subscription.Frames.Writer.TryComplete(new MachineUnreachable(_name, "The owning settings connection changed."));
        else if (!subscription.Frames.Writer.TryWrite(frame)) throw new IOException("Owning settings queue is full.");
        return true;
    }
}
