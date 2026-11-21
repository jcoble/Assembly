using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Agents;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Desktop.Settings;

/// <summary>
/// Pushes the composed <c>settings_state</c> topic (key <c>current</c>) while the Home panel is open, and the tiny
/// <c>settings_summary</c> topic (key <c>current</c>) for the Home button's progress ring and amber indicator while
/// the panel is closed. Idle with zero subscribers: no timers or polling.
/// </summary>
internal sealed class SettingsStateFeature : IFeature, IAsyncDisposable
{
    internal const string Topic = "settings_state";
    internal const string SummaryTopic = "settings_summary";
    internal const string CurrentKey = "current";

    private readonly TopicSet _topics;
    private readonly BackgroundWork _work;
    private readonly SettingsStateComposer _composer;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _watchStop;
    private string? _lastPublished;
    private string? _lastSummaryPublished;
    private bool _watching;
    private bool _disposed;

    internal SettingsStateFeature(
        IAppDatabase db,
        TopicSet topics,
        BackgroundWork work,
        IAgentLauncher launcher,
        MachinesFeature machinesFeature,
        string localEngineMachine,
        TimeProvider time,
        Func<bool>? isReconciling = null)
    {
        _topics = topics;
        _work = work;
        _composer = new SettingsStateComposer(db, launcher, machinesFeature, localEngineMachine, time, isReconciling);
        _topics.Unsubscribed += OnUnsubscribed;
    }

    public void Register(ICallRegistry calls)
    {
        calls.Topic(Topic, SettingsStateJson.Default.SettingsState, _ => CurrentKey, State);
        calls.Topic(SummaryTopic, SettingsStateJson.Default.SettingsSummary, _ => CurrentKey, SummaryState);
    }

    private IReadOnlyList<SettingsState> State(string? key, CallContext call)
    {
        if (call.Peer is not OwnerPeer)
            throw new CallFailed("Owner access is required.", "forbidden");
        if (key is not (null or CurrentKey))
            throw new CallFailed($"{Topic} only has key {CurrentKey}");

        var state = _composer.Compose(call.Cancellation);
        var json = JsonSerializer.Serialize(state, SettingsStateJson.Default.SettingsState);
        var summaryJson = JsonSerializer.Serialize(state.Summary, SettingsStateJson.Default.SettingsSummary);
        EnsureWatching(json, summaryJson);
        return [state];
    }

    private IReadOnlyList<SettingsSummary> SummaryState(string? key, CallContext call)
    {
        if (call.Peer is not OwnerPeer)
            throw new CallFailed("Owner access is required.", "forbidden");
        if (key is not (null or CurrentKey))
            throw new CallFailed($"{SummaryTopic} only has key {CurrentKey}");

        var summary = _composer.Compose(call.Cancellation).Summary;
        var summaryJson = JsonSerializer.Serialize(summary, SettingsStateJson.Default.SettingsSummary);
        EnsureWatching(null, summaryJson);
        return [summary];
    }

    private bool HasAnySubscribers() => _topics.HasSubscribers(Topic) || _topics.HasSubscribers(SummaryTopic);

    private void EnsureWatching(string? stateJson, string? summaryJson)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (stateJson is not null)
                _lastPublished = stateJson;
            if (summaryJson is not null)
                _lastSummaryPublished = summaryJson;
            if (_watching)
                return;
            _watching = true;
            var watchStop = new CancellationTokenSource();
            _watchStop = watchStop;
            _work.Start(Topic, stop => WatchAsync(stop, watchStop));
        }
    }

    private void OnUnsubscribed(string topic, string? key)
    {
        if (topic is not (Topic or SummaryTopic) || HasAnySubscribers())
            return;
        CancellationTokenSource? watchStop;
        lock (_gate)
            watchStop = _watchStop;
        if (watchStop is null)
            return;
        try
        {
            watchStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WatchAsync(CancellationToken hostStop, CancellationTokenSource watchStop)
    {
        // Yield so upstream topic subscriptions never run under the caller's Topics lock.
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStop, watchStop.Token);
        var ct = linked.Token;
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
        TopicSet.Subscription[] subscriptions = [];
        try
        {
            if (ct.IsCancellationRequested || !HasAnySubscribers())
                return;

            void Notify(string? _, string __) => wake.Writer.TryWrite(true);
            subscriptions =
            [
                _topics.Subscribe(MachinesFeature.Topic, null, _ => { }, Notify),
                _topics.Subscribe("__engine_committed", null, _ => { }, Notify),
                _topics.Subscribe("__engine_mirrored", null, _ => { }, Notify),
                _topics.Subscribe(AgentsFile.Topic, null, _ => { }, Notify),
                _topics.Subscribe(ProviderEnablementStore.Topic, null, _ => { }, Notify),
                _topics.Subscribe(LocalProviderReconciler.Topic, null, _ => { }, Notify),
            ];

            // One pass right after attaching closes any gap between State() and Subscribe().
            wake.Writer.TryWrite(true);
            await foreach (var _ in wake.Reader.ReadAllAsync(ct))
            {
                if (!HasAnySubscribers())
                    break;
                var next = _composer.Compose(ct);
                var watchFull = _topics.HasSubscribers(Topic);
                var watchSummary = _topics.HasSubscribers(SummaryTopic);
                var fullJson = watchFull ? JsonSerializer.Serialize(next, SettingsStateJson.Default.SettingsState) : null;
                var summaryJson = watchSummary ? JsonSerializer.Serialize(next.Summary, SettingsStateJson.Default.SettingsSummary) : null;
                var fullChanged = false;
                var summaryChanged = false;
                lock (_gate)
                {
                    if (fullJson is not null && fullJson != _lastPublished)
                    {
                        _lastPublished = fullJson;
                        fullChanged = true;
                    }
                    if (summaryJson is not null && summaryJson != _lastSummaryPublished)
                    {
                        _lastSummaryPublished = summaryJson;
                        summaryChanged = true;
                    }
                }
                if (fullChanged && fullJson is not null && !ct.IsCancellationRequested && _topics.HasSubscribers(Topic))
                    _topics.Publish(Topic, CurrentKey, fullJson);
                if (summaryChanged && summaryJson is not null && !ct.IsCancellationRequested && _topics.HasSubscribers(SummaryTopic))
                    _topics.Publish(SummaryTopic, CurrentKey, summaryJson);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var subscription in subscriptions)
                _topics.Unsubscribe(subscription);

            CancellationTokenSource? restart = null;
            lock (_gate)
            {
                if (ReferenceEquals(_watchStop, watchStop))
                    _watchStop = null;
                if (!_disposed && !hostStop.IsCancellationRequested && HasAnySubscribers())
                {
                    restart = new CancellationTokenSource();
                    _watchStop = restart;
                }
                else
                {
                    _watching = false;
                }
            }
            watchStop.Dispose();
            if (restart is not null)
                _work.Start(Topic, stop => WatchAsync(stop, restart));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _topics.Unsubscribed -= OnUnsubscribed;
        CancellationTokenSource? watchStop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watchStop = _watchStop;
        }
        if (watchStop is not null)
        {
            try
            {
                await watchStop.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
