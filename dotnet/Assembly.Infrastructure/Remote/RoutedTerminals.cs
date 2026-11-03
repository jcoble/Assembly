using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Terminal;

namespace Assembly.Infrastructure.Remote;

/// <summary>Routes on the owning host; the exact view's read, rather than start/list, owns remote follow.</summary>
internal sealed class RoutedTerminals : ITerminals, IAsyncDisposable
{
    private static TerminalJson Json => TerminalJson.Default;
    private readonly TerminalHost _local;
    private readonly SessionStore _sessions;
    private readonly MachineSet _machines;
    private readonly ITopics _topics;
    private readonly BackgroundWork _work;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _pending = [];
    private readonly Dictionary<string, (RemoteTerminalSocket Socket, string Id)> _followed = [];
    private bool _disposed;

    internal RoutedTerminals(TerminalHost local, SessionStore sessions, MachineSet machines, ITopics topics, BackgroundWork work)
    { _local = local; _sessions = sessions; _machines = machines; _topics = topics; _work = work; topics.Unsubscribed += Unsubscribed; }

    public async Task<TerminalInfo> StartAsync(StartArgs args, CancellationToken cancellation = default)
    {
        if (args.Owner is not { } owner || _sessions.Get(owner)?.Machine is not { } machine) return await _local.StartAsync(args, cancellation);
        var remote = Remote(machine);
        var info = await remote.CallAsync("start", args, Json.StartArgs, Json.TerminalInfo, cancellation);
        return info with { Id = remote.Qualify(info.Id) };
    }
    public async Task<IReadOnlyList<TerminalInfo>> ListAsync(string? owner, CancellationToken cancellation)
    {
        if (owner is not null && _sessions.Get(owner)?.Machine is not { }) return await _local.ListAsync(owner, cancellation);
        var all = owner is null ? _local.List().ToList() : new List<TerminalInfo>();
        var names = owner is not null ? new[] { _sessions.Get(owner)!.Machine! } : _machines.Remotes.Select(machine => machine.Name!).ToArray();
        foreach (var name in names)
        {
            var remote = Remote(name);
            var listed = await remote.CallAsync("list", new ListArgs(owner), Json.ListArgs, Json.IReadOnlyListTerminalInfo, cancellation);
            all.AddRange(listed.Select(info => info with { Id = remote.Qualify(info.Id) }));
        }
        return all;
    }
    public async ValueTask WriteAsync(string terminal, byte[] data, CancellationToken cancellation)
    {
        if (Split(terminal) is not { } remote) { await _local.WriteAsync(terminal, data, cancellation); return; }
        await remote.Socket.CallAsync("write", new WriteArgs(remote.Id, data), Json.WriteArgs, Json.Empty, cancellation);
    }
    public async Task ResizeAsync(string terminal, int cols, int rows, CancellationToken cancellation)
    {
        if (Split(terminal) is not { } remote) { await _local.ResizeAsync(terminal, cols, rows, cancellation); return; }
        await remote.Socket.CallAsync("resize", new ResizeArgs(remote.Id, cols, rows), Json.ResizeArgs, Json.Empty, cancellation);
    }
    public async Task<OutputPiece> ReadAsync(string terminal, long offset, CancellationToken cancellation)
    {
        if (Split(terminal) is not { } remote) return await _local.ReadAsync(terminal, offset, cancellation);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        bool Desired() => !_stop.IsCancellationRequested && _topics.HasSubscribers("terminal", terminal);
        if (Desired())
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _followed[terminal] = remote; }
            await remote.Socket.FollowAsync(remote.Id, stop.Token, Desired);
        }
        try { return await remote.Socket.CallAsync("read", new ReadArgs(remote.Id, offset), Json.ReadArgs, Json.OutputPiece, stop.Token); }
        catch (CallFailed error) when (error.Reason == "terminal_missing")
        {
            _topics.Publish("terminal", terminal, System.Text.Json.JsonSerializer.Serialize(new OutputRow(null, null, Unavailable: TerminalUnavailable.TerminalMissing), Json.OutputRow));
            await remote.Socket.UnfollowAsync(remote.Id, CancellationToken.None);
            lock (_gate) _followed.Remove(terminal);
            throw;
        }
    }
    public async ValueTask CloseAsync(string terminal, CancellationToken cancellation = default)
    {
        if (Split(terminal) is not { } remote) { await _local.CloseAsync(terminal, cancellation); return; }
        await remote.Socket.CallAsync("close", new TerminalKey(remote.Id), Json.TerminalKey, Json.Empty, cancellation);
        await remote.Socket.UnfollowAsync(remote.Id, cancellation);
        lock (_gate) _followed.Remove(terminal);
    }
    public ValueTask CloseOwnerAsync(string owner, CancellationToken cancellation = default)
    {
        if (_sessions.Get(owner)?.Machine is { } remote) throw new MachineUnreachable(remote, "Close this owner's terminals on its owning backend.");
        return _local.CloseOwnerAsync(owner, cancellation);
    }
    private void Unsubscribed(string topic, string? key)
    {
        if (topic != "terminal" || key is null) return;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || !_followed.ContainsKey(key)) return;
            _pending.Add(complete.Task);
            _work.Start("release terminal follow", async stopping =>
            {
                await ready.Task;
                try
                {
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping, _stop.Token);
                    (RemoteTerminalSocket Socket, string Id) remote;
                    lock (_gate) if (!_followed.TryGetValue(key, out remote)) return;
                    bool Desired() => !_stop.IsCancellationRequested && _topics.HasSubscribers("terminal", key);
                    await remote.Socket.UnfollowAsync(remote.Id, stop.Token, Desired);
                    lock (_gate) if (!Desired() && _followed.TryGetValue(key, out var current) && current == remote) _followed.Remove(key);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested || _stop.IsCancellationRequested) { }
                finally { lock (_gate) _pending.Remove(complete.Task); complete.TrySetResult(); }
            });
        }
        ready.TrySetResult();
    }
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate) { if (_disposed) return; _disposed = true; pending = _pending.ToArray(); }
        _topics.Unsubscribed -= Unsubscribed;
        await _stop.CancelAsync();
        await Task.WhenAll(pending);
        (RemoteTerminalSocket Socket, string Id)[] followed;
        lock (_gate) { followed = _followed.Values.ToArray(); _followed.Clear(); }
        foreach (var remote in followed) await remote.Socket.UnfollowAsync(remote.Id, CancellationToken.None);
        _stop.Dispose();
    }
    private RemoteTerminalSocket Remote(string name) => _machines.Named(name) is RemoteMachine machine
        ? machine.Terminals : throw new MachineUnreachable(name, $"{name} isn't connected here");
    private (RemoteTerminalSocket Socket, string Id)? Split(string id)
    {
        const string prefix = "assembly-remote-terminal://";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (id.StartsWith("assembly-remote-terminal:", StringComparison.OrdinalIgnoreCase)) throw new CallFailed("invalid remote terminal id");
            return null;
        }
        var parts = id[prefix.Length..].Split('/', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty)) throw new CallFailed("invalid remote terminal id");
        var name = Uri.UnescapeDataString(parts[0]); var terminal = Uri.UnescapeDataString(parts[1]);
        if (Uri.EscapeDataString(name) != parts[0] || Uri.EscapeDataString(terminal) != parts[1]) throw new CallFailed("invalid remote terminal id");
        return (Remote(name), terminal);
    }
}
