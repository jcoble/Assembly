using System.Text.Json;
using System.Net.WebSockets;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Agents;

/// <summary>Only selected remote keys have work. Existing mirror notifications own reconnect; there is no timer.</summary>
internal sealed class ProviderRuntimeSubscriptions : IAsyncDisposable
{
    private readonly Topics.Topics _topics;
    private readonly Func<IReadOnlyList<(RemoteMachine Machine, EngineMirror Mirror)>> _owners;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Selection> _selected = new(StringComparer.Ordinal);
    private readonly List<Task> _closing = [];
    private readonly Topics.Topics.Subscription _mirrored;
    private bool _stopping;

    internal ProviderRuntimeSubscriptions(Topics.Topics topics,
        Func<IReadOnlyList<(RemoteMachine Machine, EngineMirror Mirror)>> owners)
    {
        (_topics, _owners) = (topics, owners);
        _mirrored = topics.Subscribe("__engine_mirrored", null, _ => { }, (_, _) => Wake());
        topics.Unsubscribed += Unsubscribed;
    }

    // Both methods are memory-only, including when Topics calls them while holding its lock.
    internal IExpectedSourceGuard? Capture(string machine)
    {
        Selection? selected;
        lock (_gate) _selected.TryGetValue(machine, out selected);
        var projection = selected?.Current;
        return projection is not null && !projection.Epoch.Invalidated ? projection : null;
    }

    internal void Opened(string machine)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (!_selected.TryGetValue(machine, out var selected))
            {
                selected = new(this, machine);
                _selected.Add(machine, selected);
            }
            selected.Wake();
        }
        // Closing may have raced the out-of-lock opened callback before the selection existed.
        Unsubscribed(AgentsFile.Topic, machine);
    }

    private void Wake()
    {
        lock (_gate)
            foreach (var selected in _selected.Values) selected.Wake();
    }

    private void Unsubscribed(string topic, string? key)
    {
        if (topic != AgentsFile.Topic || key is null || _topics.HasSubscribers(topic, key)) return;
        lock (_gate)
        {
            if (!_selected.Remove(key, out var selected)) return;
            selected.Revoke();
            _closing.RemoveAll(task => task.IsCompletedSuccessfully);
            _closing.Add(Task.Run(async () =>
            {
                await selected.StopAsync();
                // A new subscriber may have arrived after the last-key notification was produced.
                if (_topics.HasSubscribers(AgentsFile.Topic, key)) Opened(key);
            }));
        }
    }

    internal sealed class Projection(Epoch epoch, ProviderRuntimeChanged value) : IExpectedSourceGuard
    {
        internal Epoch Epoch { get; } = epoch;
        internal ProviderRuntimeChanged Value { get; } = value;
        public CancellationToken Changed => Epoch.Changed;
        public void EnsureCurrent()
        {
            if (Epoch.Invalidated) throw new CallFailed("The selected settings owner changed.", "source_changed");
        }
        public void Dispose() { } // The selection owns the epoch, including all frames already queued from it.
    }

    internal sealed class Epoch : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private int _lost;
        internal Epoch(EngineMirror.LiveMirrorBinding live, CancellationToken stopping)
        {
            Live = live;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping, live.Invalidated);
            Changed = _stop.Token;
        }
        internal CancellationToken Changed { get; }
        internal EngineMirror.LiveMirrorBinding Live { get; }
        internal bool Invalidated => Volatile.Read(ref _lost) != 0 || Live.IsInvalidated || Changed.IsCancellationRequested;
        // A token is stored separately because queued deliveries can outlive disposal of the CTS.
        internal CancellationToken Token => Changed;
        internal void Revoke() => Interlocked.Exchange(ref _lost, 1);
        internal async Task CancelAsync() { Revoke(); await _stop.CancelAsync(); }
        public void Dispose() => _stop.Dispose();
    }

    private sealed class Selection
    {
        private readonly ProviderRuntimeSubscriptions _owner;
        private readonly string _machine;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly Task _run;
        private readonly HashSet<EngineMirror> _observed = [];
        private Projection? _current;
        private Epoch? _epoch;
        private EngineMirror.LiveMirrorBinding? _attempted;
        private Task _follow = Task.CompletedTask;
        internal Projection? Current => Volatile.Read(ref _current);

        internal Selection(ProviderRuntimeSubscriptions owner, string machine)
        {
            (_owner, _machine) = (owner, machine);
            _run = Task.Run(RunAsync);
        }
        internal void Wake() => _wake.Writer.TryWrite(true);
        internal void Revoke() => Volatile.Read(ref _epoch)?.Revoke();
        internal async Task StopAsync()
        {
            Revoke();
            await _stop.CancelAsync();
            try { await _run; }
            finally { _stop.Dispose(); }
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var _ in _wake.Reader.ReadAllAsync(_stop.Token))
                {
                    var owners = _owner._owners();
                    var mirrors = owners.Select(owner => owner.Mirror).ToHashSet();
                    foreach (var old in _observed.Where(old => !mirrors.Contains(old)).ToArray())
                    { old.CaughtUp -= Wake; _observed.Remove(old); }
                    foreach (var mirror in mirrors)
                        if (_observed.Add(mirror)) mirror.CaughtUp += Wake;

                    (RemoteMachine Machine, EngineMirror.LiveMirrorBinding Live)? matched = null;
                    foreach (var candidate in owners)
                    {
                        EngineMirror.LiveMirrorBinding? live;
                        try { live = await candidate.Mirror.CaptureLiveBindingAsync(_stop.Token); }
                        catch (Exception error) when (error is MachineUnreachable or ObjectDisposedException) { continue; }
                        if (live?.Machine == _machine) { matched = (candidate.Machine, live); break; }
                    }
                    if (matched is { } same && ReferenceEquals(_epoch?.Live, same.Live) && _epoch is { Invalidated: false }) continue;
                    await FinishEpochAsync();
                    if (matched is not { } next) continue;
                    // A refused/malformed stream retries only on a real owner generation change, never a self-wake loop.
                    if (ReferenceEquals(_attempted, next.Live)) continue;
                    _attempted = next.Live;
                    var epoch = new Epoch(next.Live, _stop.Token);
                    Volatile.Write(ref _epoch, epoch);
                    _follow = FollowAsync(next.Machine, epoch);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                foreach (var mirror in _observed) mirror.CaughtUp -= Wake;
                await FinishEpochAsync();
            }
        }

        private async Task FollowAsync(RemoteMachine remote, Epoch epoch)
        {
            try
            {
                await foreach (var changed in remote.ProviderRuntimeFramesAsync(epoch.Live, epoch.Token))
                {
                    if (epoch.Invalidated) break;
                    // This one follow owns publication until it is joined before the next epoch is installed.
                    Volatile.Write(ref _current, new Projection(epoch, changed));
                    _owner._topics.Publish(AgentsFile.Topic, _machine,
                        JsonSerializer.Serialize(changed, ProviderRuntimeJson.Default.ProviderRuntimeChanged), keyedOnly: true);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or MachineUnreachable or CallFailed or IOException or WebSocketException or ObjectDisposedException
                or JsonException or KeyNotFoundException or InvalidOperationException)
            { }
            finally { epoch.Revoke(); Volatile.Write(ref _current, null); Wake(); }
        }

        private async Task FinishEpochAsync()
        {
            if (_epoch is not { } epoch) return;
            await epoch.CancelAsync();
            await _follow;
            epoch.Dispose();
            Volatile.Write(ref _epoch, null);
            Volatile.Write(ref _current, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] closing;
        _topics.Unsubscribed -= Unsubscribed;
        _mirrored.Dispose();
        lock (_gate)
        {
            _stopping = true;
            foreach (var selected in _selected.Values) selected.Revoke();
            closing = _closing.Concat(_selected.Values.Select(selected => Task.Run(selected.StopAsync))).ToArray();
            _selected.Clear();
        }
        await Task.WhenAll(closing);
    }
}
