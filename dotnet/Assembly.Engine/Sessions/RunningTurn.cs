namespace Assembly.Engine.Sessions;

/// <summary>The turn a thread is running: its id, its stage times, how to stop it, and its session once it's open.</summary>
internal sealed class RunningTurn(string runId, StageTimes stages)
{
    public string RunId { get; } = runId;

    public StageTimes Stages { get; } = stages;

    /// <summary>Cancelled by Stop. Before the session is open, it cancels the open; after, Stop also asks the agent to cancel.</summary>
    public CancellationTokenSource Stopping { get; } = new();

    /// <summary>Set under the host's lock once the session is open.</summary>
    public OpenSession? Session { get; set; }

    public Task Done { get; set; } = Task.CompletedTask;

    /// <summary>The agent started this run itself, on a session kept open for background work; no prompt is waiting on it.</summary>
    public bool SelfStarted { get; init; }

    /// <summary>Set when a self-started run ends, which completes its <see cref="Done"/>.</summary>
    public TaskCompletionSource? Ended { get; init; }

    /// <summary>When a self-started run last heard from the agent, in event time. Guarded by the host's lock.</summary>
    public long LastEventAt { get; set; }
}
