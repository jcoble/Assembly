using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Hosting;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Engine.Needs;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Engine.Relays;

/// <summary>One pushed coordinator for durable owning messages; existing runtimes own provider work.</summary>
internal sealed partial class MachineRelay : IAsyncDisposable
{
    private readonly EngineRuntime _runtime;
    private readonly NeedAnswers _needs;
    private readonly IEngineRuntime _engine;
    private readonly MachineForward _forward;
    private readonly RemoteConnections _connections;
    private readonly TopicSet _topics;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _stopping;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _gate = new();
    private readonly HashSet<EngineMirror> _watched = [];
    private readonly HashSet<string> _retryWakes = new(StringComparer.Ordinal);
    private TopicSet.Subscription? _mirrored;
    private IDisposable? _localEvents;
    private readonly ConcurrentDictionary<string, EngineMirror.LiveMirrorBinding> _observedLive = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _remoteEvents = new(StringComparer.Ordinal);
    private Task _run = Task.CompletedTask;
    private Task? _dispose;
    private bool _started;

    internal MachineRelay(EngineRuntime runtime, MachineForward forward, RemoteConnections connections, TopicSet topics, TimeProvider time, TextWriter log, NeedAnswers needs)
    {
        (_runtime, _engine, _forward, _connections, _topics, _time, _log, _needs) = (runtime, runtime, forward, connections, topics, time, log, needs);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(runtime.Stopping); _stopping = _stop.Token;
    }

    public void Start()
    {
        TaskCompletionSource ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_started) return;
            _started = true; ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _localEvents = _runtime.Publisher.Register(OnEvent, out _, Wake);
            _mirrored = _topics.Subscribe("__engine_mirrored", null, _ => { }, MirrorNotice);
            _run = RunAfter(ready.Task);
        }
        ready.SetResult(); Wake();
    }
    private void Wake() => _wake.Writer.TryWrite(true);
    private void OnEvent(EngineEvent item)
    {
        if (item.EntityKind is "message" or "session" or "handoff" || item.Kind is "parent_need.requested" or "parent_need.changed") Wake();
    }
    private void MirrorNotice(string? machine, string json)
    {
        try
        {
            if (machine is null || !_observedLive.TryGetValue(machine, out var observed) || observed.IsInvalidated || JsonSerializer.Deserialize(json, EngineJson.Default.MirrorNotice)?.Reset == true) Wake();
        }
        catch (JsonException) { Wake(); }
    }
    private async Task RunAfter(Task ready)
    {
        await ready.ConfigureAwait(false);
        try
        {
            await foreach (var signal in _wake.Reader.ReadAllAsync(_stopping).ConfigureAwait(false))
            {
                try
                {
                    var endpoints = await CaptureEndpoints(_stopping);
                    Candidate? after = null;
                    while (true)
                    {
                        var candidates = _engine.Read(scope => Candidates(scope, endpoints, after), _stopping);
                        foreach (var candidate in candidates)
                        {
                            _stopping.ThrowIfCancellationRequested();
                            try { await RelayOne(candidate, EndpointFor(candidate.Source, endpoints), EndpointFor(candidate.Target, endpoints), _stopping); }
                            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { throw; }
                            catch (Exception error) { Diagnostic(error); }
                        }
                        if (candidates.Count < 200) break;
                        after = candidates[^1];
                    }
                    await RelayParentNeeds(endpoints, _stopping);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { throw; }
                catch (Exception error) { Diagnostic(error); }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task<Dictionary<string, Endpoint>> CaptureEndpoints(CancellationToken ct)
    {
        var owners = _connections.CaptureMirrorOwners(); var current = owners.Select(owner => owner.Mirror).ToHashSet();
        foreach (var old in _watched.Where(old => !current.Contains(old)).ToArray()) { old.CaughtUp -= Wake; _watched.Remove(old); }
        foreach (var owner in owners) if (_watched.Add(owner.Mirror)) owner.Mirror.CaughtUp += Wake;
        Dictionary<string, Endpoint> result = new(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            var live = await owner.Mirror.CaptureLiveBindingAsync(ct);
            if (live is null) continue;
            if (live.Machine == _runtime.Machine || !result.TryAdd(live.Machine, new(live.Machine, owner.Machine, owner.Mirror, live)))
                throw new CallFailed("ambiguous engine owner");
        }
        foreach (var machine in _observedLive.Keys.Where(machine => !result.ContainsKey(machine))) _observedLive.TryRemove(machine, out _);
        foreach (var endpoint in result.Values) _observedLive[endpoint.Machine] = endpoint.Live!;
        foreach (var machine in _remoteEvents.Keys.Where(machine => !result.ContainsKey(machine)))
            if (_remoteEvents.TryRemove(machine, out var registration)) registration.Dispose();
        foreach (var machine in result.Keys)
            if (!_remoteEvents.ContainsKey(machine)) _remoteEvents[machine] = _runtime.PublisherFor(machine).Register(OnEvent, out _, Wake);
        return result;
    }
    private Endpoint EndpointFor(string machine, IReadOnlyDictionary<string, Endpoint> endpoints) => machine == _runtime.Machine
        ? new(machine, null, null, null) : endpoints[machine];
    private sealed record Endpoint(string Machine, RemoteMachine? Remote, EngineMirror? Mirror, EngineMirror.LiveMirrorBinding? Live)
    {
        internal async Task RequireCurrent(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Mirror is not null && !await Mirror.IsCurrentLiveBindingAsync(Live!, ct)) throw new CallFailed("relay_connection_changed");
        }
    }
    private void Diagnostic(Exception error)
    {
        try { _log.WriteLine($"message relay: {error.GetType().Name}: {error.Message}"); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? ready = null; Task result;
        lock (_gate)
        {
            if (_dispose is null) { ready = new(TaskCreationOptions.RunContinuationsAsynchronously); _dispose = DisposeAfter(ready.Task); }
            result = _dispose;
        }
        ready?.SetResult(); return new(result);
    }
    private async Task DisposeAfter(Task ready)
    {
        await ready.ConfigureAwait(false);
        _localEvents?.Dispose();
        if (_mirrored is not null) _topics.Unsubscribe(_mirrored);
        try { await _stop.CancelAsync(); } catch (Exception error) { Diagnostic(error); }
        try { await _run.ConfigureAwait(false); }
        finally
        {
            foreach (var mirror in _watched) mirror.CaughtUp -= Wake;
            _watched.Clear(); _retryWakes.Clear();
            foreach (var registration in _remoteEvents.Values) registration.Dispose();
            _remoteEvents.Clear(); _observedLive.Clear(); _stop.Dispose();
        }
    }
}
