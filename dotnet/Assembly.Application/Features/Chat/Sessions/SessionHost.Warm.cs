using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionHost
{
    private sealed class WarmRun(string target, CancellationTokenSource stop, long generation)
    {
        public string Target { get; } = target;
        public CancellationTokenSource Stop { get; } = stop;
        public long Generation { get; } = generation;
        public Task<SessionConfig> Task { get; set; } = null!;
    }
    private WarmRun? _warm;
    private long _warmGeneration;

    public Task<SessionConfig> WarmConfigAsync(string targetId, CancellationToken ct) => WarmOrPickAsync(targetId, null, ct);

    public Task<SessionConfig> SetAndWarmConfigAsync(string configId, string value, string targetId, CancellationToken ct) =>
        WarmOrPickAsync(targetId, new KeyValuePair<string, string>(configId, value), ct);

    private async Task<SessionConfig> WarmOrPickAsync(string target, KeyValuePair<string, string>? pick, CancellationToken ct)
    {
        var info = _sessionStore.Get(_sessionId) ?? throw new CallFailed("unknown session");
        _opener.ValidateTarget(info.Agent, target);
        Task? opening;
        Task<SessionConfig>? previous;
        WarmRun? replaced = null;
        var owns = false;
        lock (_gate)
        {
            if (_shuttingDown) throw new CallFailed("The backend is shutting down.");
            opening = _turn is { Session: null } turn ? turn.Opened.Task : null;
            previous = _warm?.Task;
            if (_warm is { } prior && (prior.Target != target || prior.Stop.IsCancellationRequested)) replaced = prior;
            if (opening is null && previous is null && (_turn?.Session ?? _kept?.Session) is { } live)
            {
                if (live.Adapter.TargetId != target) throw new CallFailed("The open agent configuration changed.");
                if (pick is null) return _sessionStore.Config(_sessionId);
            }
            else if (opening is null && previous is null)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var run = new WarmRun(target, stop, Interlocked.Increment(ref _warmGeneration));
                _warm = run;
                var ownedPick = pick;
                run.Task = Task.Run(() => RunWarmAsync(run, ownedPick));
                previous = run.Task;
                owns = true;
                pick = null; // this request's pick is owned by RunWarmAsync
            }
        }
        if (replaced is not null)
        {
            try { await replaced.Stop.CancelAsync(); } catch (ObjectDisposedException) { }
            try { await replaced.Task.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
            return await WarmOrPickAsync(target, pick, ct);
        }
        if (opening is not null)
        {
            await opening.WaitAsync(ct);
            return await WarmOrPickAsync(target, pick, ct);
        }
        if (previous is not null)
        {
            var result = owns ? await previous : await previous.WaitAsync(ct);
            if (pick is null) return result;
            return await WarmOrPickAsync(target, pick, ct);
        }
        await SetConfigAsync(pick!.Value.Key, pick.Value.Value, ct, target);
        return _sessionStore.Config(_sessionId);
    }

    private async Task<SessionConfig> RunWarmAsync(WarmRun run, KeyValuePair<string, string>? pick)
    {
        using var deadline = new CancellationTokenSource(ComposerConfigDiscovery.Limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token, deadline.Token);
        var ct = linked.Token;
        var held = false;
        OpenSession? opened = null;
        try
        {
            await _session.WaitAsync(ct);
            held = true;
            void Validate()
            {
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _warmGeneration) != run.Generation)
                    throw new OperationCanceledException("A real turn superseded this settings read.", ct);
            }
            Validate();
            opened = await _opener.OpenAsync(_sessionId, "", new StageTimes(_time), ct,
                configWarm: true, targetId: run.Target, pick: pick, validate: Validate);
            Validate();
            return _sessionStore.Config(_sessionId);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !run.Stop.IsCancellationRequested)
        { throw new CallFailed("Provider settings did not answer within 15 seconds."); }
        finally
        {
            if (opened is not null)
            {
                opened.Adapter.Updates.Forget(opened.AcpSessionId);
                opened.Adapter.Link.Client.SetSessionPolicy(opened.AcpSessionId, null);
                await opened.Pool.ReleaseWarmAsync(opened.Adapter, opened.AcpSessionId);
            }
            if (held) _session.Release();
            lock (_gate) if (ReferenceEquals(_warm, run)) _warm = null;
            run.Stop.Dispose();
        }
    }

    internal async Task StopWarmForShutdownAsync()
    {
        WarmRun? run;
        lock (_gate)
        {
            _shuttingDown = true;
            Interlocked.Increment(ref _warmGeneration);
            run = _warm;
        }
        if (run is null) return;
        try { await run.Stop.CancelAsync(); } catch (ObjectDisposedException) { }
        try { await run.Task; }
        catch (Exception error) { _log.WriteLine($"shutdown: settings read ended: {error.Message}"); }
    }
}
