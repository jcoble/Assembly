using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;

using Assembly.Application.Features.Chat.Sessions;
namespace Assembly.Application.Common.Agents;

/// <summary>An adapter process and what every session on it needs: its initialize answer, and its updates by session.</summary>
internal sealed record RunningAdapter(IAgentLink Link, InitializeResponse Init, UpdateRouter Updates);

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
public sealed class SharedAdapter(IAgentLauncher launcher, string agentId, TimeProvider time, TextWriter log) : IAsyncDisposable
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
    internal async Task<RunningAdapter> GetAsync(StageTimes stages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _idle?.Cancel();
            _idle?.Dispose();
            _idle = null;
            if (_running is not { } running || running.Link.Exited.IsCompleted)
            {
                await StopAsync();
                var link = await launcher.LaunchAsync(agentId, stages, cancellationToken);
                try
                {
                    var air = launcher.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Air;
                    var init = await link.Client.InitializeAsync(ClientInfo, air, cancellationToken);
                    stages.Mark("initialize");
                    running = _running = new RunningAdapter(link, init, new UpdateRouter(link.Client.Updates));
                }
                catch
                {
                    await link.DisposeAsync();
                    throw;
                }
            }

            _sessions++;
            return running;
        }
        finally
        {
            _gate.Release();
        }
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
                if (_running == running)
                {
                    _running = null;
                    _retired[running] = _sessions;
                    _sessions = 0;
                }
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
        await _gate.WaitAsync();
        try
        {
            _idle?.Cancel();
            await _idleTask;
            await StopAsync();
            foreach (var retired in _retired.Keys)
                await EndAsync(retired);
            _retired.Clear();
            await Task.WhenAll(_stopping);
        }
        finally
        {
            _gate.Release();
        }
    }
}
