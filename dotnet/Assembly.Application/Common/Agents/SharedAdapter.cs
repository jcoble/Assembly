using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Agents;

using Assembly.Application.Features.Chat.Sessions;
namespace Assembly.Application.Common.Agents;

/// <summary>An adapter process and what every session on it needs: its initialize answer, and its updates by session.</summary>
internal sealed record RunningAdapter(IAgentLink Link, InitializeResponse Init, UpdateRouter Updates, string TargetId);

/// <summary>
/// The one adapter process an agent's sessions share on this machine (and so one Codex app-server for every Codex
/// session). The first send starts and initializes it; a send after it has exited starts a new one. Each session's
/// turn opens its session on it and closes it when the turn ends. Once its last session closes, it stays up for
/// <see cref="IdleGrace"/>, so a conversation's next turn finds it warm, and then stops. A close that doesn't answer
/// within <see cref="CloseLimit"/> retires the adapter: it serves only the sessions already on it and stops when the
/// last of them is released, and the next send starts a new one. The timed-out session's next open waits for the
/// retired process to exit, so two processes never have the same ACP session open (Claude Code doesn't guard against
/// it; Codex refuses the second writer).
/// </summary>
public sealed partial class SharedAdapter(IAgentLauncher launcher, string agentId, TimeProvider time, TextWriter log) : IAsyncDisposable
{
    /// <summary>How long an adapter with no open session stays up.</summary>
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long a session's close is given before its adapter is retired.</summary>
    public static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(5);

    /// <summary>The clock its limits are timed on.</summary>
    internal TimeProvider Time => time;

    private static readonly Implementation ClientInfo = new("assembly-backend", "0.1.0");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunningAdapter? _running;
    private bool _disposed;
    private int _sessions;
    private CancellationTokenSource? _idle;
    // The wait for the idle grace to end, awaited by the dispose.
    private Task _idleTask = Task.CompletedTask;
    // Each retired adapter, with the sessions still on it.
    private readonly Dictionary<RunningAdapter, int> _retired = [];
    // Each ACP session whose close timed out, with its retired adapter's exit.
    private readonly Dictionary<string, Task> _closing = [];
    // Retired adapters being stopped, so disposing this waits for them too.
    private readonly List<Task> _stopping = [];

    /// <summary>The running adapter, started if need be, for one more session; <see cref="ReleaseAsync"/> gives it back.</summary>
    internal Task<RunningAdapter> GetAsync(StageTimes stages, CancellationToken cancellationToken) =>
        GetForSessionAsync(null, stages, cancellationToken);

    // Native-id admission and borrowing happen under the SAME gate. A detached/retired writer must exit first.
    internal async Task<RunningAdapter> GetForSessionAsync(string? nativeId, StageTimes stages, CancellationToken cancellationToken, string? targetId = null, Action<IAgentLink>? observeLink = null, AgentDescriptor? suppliedDescriptor = null, Action? initializeAuthRequired = null)
    {
        while (true)
        {
            Task? wait = null;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var suppliedEnvironment = suppliedDescriptor?.Env;
                if (suppliedDescriptor is not null && suppliedEnvironment is null)
                    throw new Assembly.Application.Common.Calls.CallFailed("The selected agent configuration changed. Select it again.");
                var captured = suppliedDescriptor is null
                    ? ConfigTargetIdentity.Capture(launcher, agentId, targetId)
                    : (Descriptor: suppliedDescriptor, Environment: suppliedEnvironment!,
                        TargetId: ConfigTargetIdentity.Of(suppliedDescriptor, suppliedEnvironment!));
                if (suppliedDescriptor is not null)
                {
                    ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment);
                    if (captured.TargetId != targetId)
                        throw new Assembly.Application.Common.Calls.CallFailed("The selected agent configuration changed. Select it again.");
                }
                _idle?.Cancel();
                _idle?.Dispose();
                _idle = null;
                foreach (var key in _closing.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToList()) _closing.Remove(key);
                foreach (var key in _detached.Where(x => x.Value.Link.Exited.IsCompleted).Select(x => x.Key).ToList()) _detached.Remove(key);
                if (_running is { } old && (old.Link.Exited.IsCompleted || old.TargetId != captured.TargetId))
                    _ = RetireLocked(old); // Stop is retained in _stopping.
                if (nativeId is not null && _closing.TryGetValue(nativeId, out var closing)) wait = closing;
                else if (nativeId is not null && _detached.TryGetValue(nativeId, out var detached) && detached != _running) wait = detached.Link.Exited;
                if (wait is null)
                {
                    if (_running is not { } running)
                    {
                        // Never re-look up by id after checking a descriptor: launch exactly the captured target.
                        var link = await launcher.LaunchConfiguredAsync(captured.Descriptor, captured.Environment, stages, cancellationToken);
                        try
                        {
                            observeLink?.Invoke(link);
                            InitializeResponse init;
                            try { init = await link.Client.InitializeAsync(ClientInfo, launcher.CanRunTerminalAuthentication, cancellationToken); }
                            catch (JsonRpcException error) when (error.Code == -32000) { initializeAuthRequired?.Invoke(); throw; }
                            ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment);
                            stages.Mark("initialize");
                            running = _running = new RunningAdapter(link, init, new UpdateRouter(link.Client), captured.TargetId);
                        }
                        catch { await link.DisposeAsync(); throw; }
                    }
                    else observeLink?.Invoke(running.Link);
                    _sessions++;
                    return running;
                }
            }
            finally { _gate.Release(); }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private readonly Dictionary<string, RunningAdapter> _detached = [];

    private Task? RetireLocked(RunningAdapter running)
    {
        if (_running != running) return null;
        _running = null;
        Task? stopping = null;
        _idle?.Cancel();
        _idle?.Dispose();
        _idle = null;
        if (_sessions > 0) _retired[running] = _sessions;
        else stopping = TrackStopLocked(running);
        _sessions = 0;
        return stopping;
    }

    private Task TrackStopLocked(RunningAdapter running)
    {
        _stopping.RemoveAll(task => task.IsCompleted);
        var stopping = EndAsync(running);
        _stopping.Add(stopping);
        return stopping;
    }

    internal async Task RetireAsync(RunningAdapter expected, string? nativeId)
    {
        Task? stopping;
        Task? idle;
        await _gate.WaitAsync();
        try
        {
            // A stale capture must not join or cancel the replacement's idle owner.
            idle = _running == expected ? _idleTask : null;
            stopping = RetireLocked(expected);
            if (nativeId is not null) _closing[nativeId] = expected.Link.Exited;
        }
        finally { _gate.Release(); }
        if (idle is not null) await idle;
        if (stopping is not null) await stopping;
    }

    internal async Task ReleaseWarmAsync(RunningAdapter expected, string? nativeId)
    {
        Task? stopping = null;
        await _gate.WaitAsync();
        try
        {
            if (nativeId is not null) _detached[nativeId] = expected;
            if (_retired.TryGetValue(expected, out var count))
            {
                if (count > 1) _retired[expected] = count - 1;
                else { _retired.Remove(expected); stopping = TrackStopLocked(expected); }
            }
            else if (_running == expected && --_sessions == 0)
            {
                _running = null;
                stopping = TrackStopLocked(expected);
            }
        }
        finally { _gate.Release(); }
        if (stopping is not null) await stopping;
    }

    /// <summary>
    /// A session from <see cref="GetAsync"/> is closed (or never opened). The last one starts the idle grace, or, on a
    /// retired adapter, stops it.
    /// </summary>
    internal async Task ReleaseAsync(RunningAdapter running)
    {
        Task? stopping = null;
        await _gate.WaitAsync();
        try
        {
            if (_retired.TryGetValue(running, out var left))
            {
                if (left > 1)
                    _retired[running] = left - 1;
                else
                {
                    _retired.Remove(running);
                    _stopping.RemoveAll(task => task.IsCompleted);
                    _stopping.Add(stopping = EndAsync(running));
                }
                return;
            }
            if (_running != running) return;
            if (--_sessions > 0)
                return;
            _idle = new CancellationTokenSource();
            _idleTask = StopWhenIdleAsync(_idle.Token);
        }
        finally
        {
            _gate.Release();
            // Outside the gate: a stuck process takes its grace to end, and other sessions shouldn't wait on it.
            if (stopping is not null)
                await stopping;
        }
    }

    /// <summary>
    /// Closes an ACP session. One that doesn't answer within <see cref="CloseLimit"/> retires its adapter and throws
    /// <see cref="TimeoutException"/>.
    /// </summary>
    internal async Task CloseAsync(RunningAdapter running, string acpSessionId)
    {
        try
        {
            await running.Link.Client.CloseSessionAsync(acpSessionId, CancellationToken.None).WaitAsync(CloseLimit, time);
        }
        catch (TimeoutException)
        {
            await _gate.WaitAsync();
            try
            {
                _ = RetireLocked(running); // Stop is retained in _stopping.
                _closing[acpSessionId] = running.Link.Exited;
            }
            finally
            {
                _gate.Release();
            }
            throw;
        }
    }

    /// <summary>Waits until no retired adapter can still have <paramref name="acpSessionId"/> open.</summary>
    internal async Task ClosedAsync(string? acpSessionId, CancellationToken cancellationToken)
    {
        Task? exited;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (acpSessionId is null || !_closing.TryGetValue(acpSessionId, out exited))
                return;
            if (exited.IsCompleted)
                _closing.Remove(acpSessionId);
        }
        finally
        {
            _gate.Release();
        }
        await exited.WaitAsync(cancellationToken);
    }

    private async Task StopWhenIdleAsync(CancellationToken reopened)
    {
        try
        {
            await Task.Delay(IdleGrace, time, reopened);
            await _gate.WaitAsync(reopened);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_sessions == 0)
                await StopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, null) is { } running)
            await EndAsync(running);
    }

    // Disposes the link, which completes its updates and so ends the router's pump, and waits up to the close limit
    // for the pump.
    private async Task EndAsync(RunningAdapter running)
    {
        await running.Link.DisposeAsync();
        try
        {
            await running.Updates.Completion.WaitAsync(CloseLimit, time);
        }
        catch (TimeoutException)
        {
            log.WriteLine($"shutdown: update pump for {agentId} still running after {CloseLimit.TotalSeconds:0.#} s");
        }
    }

    /// <summary>Ends the adapter and any retired ones, and with them every session still open on them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_providerStop.IsCancellationRequested) await _providerStop.CancelAsync();
        Task[] stopping = [];
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _idle?.Cancel();
            await _idleTask;
            _idle?.Dispose();
            _idle = null;
            await StopAsync();
            foreach (var retired in _retired.Keys)
                await EndAsync(retired);
            _retired.Clear();
            stopping = _stopping.ToArray();
            _closing.Clear();
            _detached.Clear();
        }
        finally
        {
            _gate.Release();
        }
        await Task.WhenAll(stopping);
        await DrainProviderWorkAsync();
        _providerStop.Dispose();
    }
}
