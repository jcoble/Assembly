using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Git;

public sealed partial class GitOperations
{
    internal sealed record RemoteObservation(RemoteMachine Machine, ClientWebSocket Socket, RemoteMachine.OwnerConnectionBinding Owner, Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent);
    private sealed class RemoteWatch(string machine, Guid id, Func<CancellationToken, Task<RemoteObservation>> capture)
    {
        internal readonly string Machine = machine;
        internal readonly Guid Id = id;
        internal readonly Func<CancellationToken, Task<RemoteObservation>> Capture = capture;
        internal readonly HashSet<string> Connections = [];
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GitOperationStatus? Latest;
        internal GitOperationArgs? Request;
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, RemoteWatch> _remoteWatches = [];

    internal GitOperationStatus? ObserveRemote(string machine, Guid id, string connection,
        RemoteMachine initial, Func<CancellationToken, Task<RemoteObservation>> capture)
    {
        var key = machine + "/" + id.ToString("D");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_remoteWatches.TryGetValue(key, out var watch))
            {
                watch = new(machine, id, capture);
                _remoteWatches.Add(key, watch);
                _running.Add(watch.Completion.Task);
                var owned = watch;
                _work.Start("observe remote Git", async stopping =>
                {
                    try { await ObserveRemoteAsync(key, owned, initial, stopping); }
                    finally
                    {
                        lock (_gate)
                        {
                            _running.Remove(owned.Completion.Task);
                            if (_remoteWatches.TryGetValue(key, out var current) && ReferenceEquals(current, owned)) _remoteWatches.Remove(key);
                        }
                        owned.Stop.Dispose();
                        owned.Completion.TrySetResult();
                    }
                });
            }
            watch.Connections.Add(connection);
            return watch.Latest;
        }
    }

    private async Task ObserveRemoteAsync(string key, RemoteWatch watch, RemoteMachine initial, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _stop.Token, watch.Stop.Token);
        var ct = linked.Token;
        // Reconnection is event-driven. It never replays the operation's start.
        var opened = Channel.CreateBounded<bool>(1);
        void Wake() => opened.Writer.TryWrite(true);
        RemoteMachine? listened = initial;
        listened.SocketOpened += Wake;
        var reconnecting = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var source = await watch.Capture(ct);
                    if (!ReferenceEquals(listened, source.Machine))
                    {
                        if (listened is not null) listened.SocketOpened -= Wake;
                        listened = source.Machine;
                        listened.SocketOpened += Wake;
                    }
                    source.EnsureCurrent();
                    if (reconnecting)
                    {
                        GitOperationArgs? request;
                        lock (_gate) request = watch.Request;
                        if (request is not null)
                        {
                            var reply = await source.Machine.CallAsync("git", "operation", JsonSerializer.SerializeToElement(request, GitOperationsJson.Default.GitOperationArgs),
                                false, ct, watch.Machine, source.Socket, source.Owner, source.Live);
                            source.EnsureCurrent();
                            Accept(reply);
                        }
                    }
                    await foreach (var frame in source.Machine.GitOperationFramesAsync(watch.Id, watch.Machine, source.Socket, ct))
                    {
                        source.EnsureCurrent();
                        var kind = frame.GetProperty("t").GetString();
                        if (kind == "state" && frame.TryGetProperty("items", out var items))
                        {
                            foreach (var item in items.EnumerateArray()) Accept(item);
                            watch.Ready.TrySetResult();
                        }
                        else if (kind == "change" && frame.TryGetProperty("item", out var item)) Accept(item);
                        else if (kind == "error") throw new IOException("Remote Git observation refused.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // No replacement status/instance/version is invented after losing the owning socket.
                    watch.Ready.TrySetException(new IOException("The remote Git observer did not become ready."));
                }
                await opened.Reader.ReadAsync(ct);
                reconnecting = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { watch.Ready.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true)); if (listened is not null) listened.SocketOpened -= Wake; }

        void Accept(JsonElement item)
        {
            var status = item.Deserialize(GitOperationsJson.Default.GitOperationStatus);
            if (status is null || status.Machine != watch.Machine || status.OperationId != watch.Id) throw new IOException("Remote Git identity changed.");
            lock (_gate)
            {
                if (watch.Latest is { } prior && prior.InstanceId == status.InstanceId && status.Version <= prior.Version) return;
                watch.Latest = status;
            }
            _topics.Publish("gitOperation", key, JsonSerializer.Serialize(status, GitOperationsJson.Default.GitOperationStatus));
        }
    }

    internal async Task WaitRemoteObservationAsync(string machine, Guid id, string session, string? action, CancellationToken ct)
    {
        Task? ready = null;
        lock (_gate)
        {
            if (_remoteWatches.TryGetValue(machine + "/" + id.ToString("D"), out var watch))
            {
                // A later read uses the original actual session/id. It cannot start or replay work.
                watch.Request = new(id, session, action);
                ready = watch.Ready.Task;
            }
        }
        if (ready is not null) await ready.WaitAsync(ct);
    }

    private void CloseRemote(string connection)
    {
        lock (_gate)
        {
            foreach (var pair in _remoteWatches.ToArray())
            {
                pair.Value.Connections.Remove(connection);
                if (pair.Value.Connections.Count == 0) StopRemote(pair.Key);
            }
        }
    }
    private void StopRemote(string key)
    {
        lock (_gate)
            if (_remoteWatches.Remove(key, out var watch)) watch.Stop.Cancel();
    }
}
