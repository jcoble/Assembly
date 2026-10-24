using Assembly.Engine.Acp.Messages;
using Assembly.Engine.Agents;

namespace Assembly.Engine.Sessions;

/// <summary>An adapter process and what every session on it needs: its initialize answer, and its updates by session.</summary>
internal sealed record RunningAdapter(IAgentLink Link, InitializeResponse Init, UpdateRouter Updates);

/// <summary>
/// The one adapter process an agent's threads share on this machine (and so one Codex app-server for every Codex
/// thread). The first send starts and initializes it; a send after it has exited starts a new one. Each thread's
/// turn opens its session on it and closes it when the turn ends. Once its last session closes, it stays up for
/// <see cref="IdleGrace"/>, so a conversation's next turn finds it warm, and then stops.
/// </summary>
public sealed class SharedAdapter(IAgentLauncher launcher, string agentId, TimeProvider time) : IAsyncDisposable
{
    /// <summary>How long an adapter with no open session stays up.</summary>
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);

    private static readonly Implementation ClientInfo = new("assembly-backend", "0.1.0");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunningAdapter? _running;
    private int _sessions;
    private CancellationTokenSource? _idle;

    /// <summary>The running adapter, started if need be, for one more session; <see cref="ReleaseAsync"/> gives it back.</summary>
    internal async Task<RunningAdapter> GetAsync(StageTimes stages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _idle?.Cancel();
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

    /// <summary>A session from <see cref="GetAsync"/> is closed (or never opened). The last one starts the idle grace.</summary>
    internal async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (--_sessions > 0)
                return;
            _idle = new CancellationTokenSource();
            _ = StopWhenIdleAsync(_idle.Token);
        }
        finally
        {
            _gate.Release();
        }
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
            await running.Link.DisposeAsync();
    }

    /// <summary>Ends the adapter, and with it every session still open on it.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _idle?.Cancel();
            await StopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
