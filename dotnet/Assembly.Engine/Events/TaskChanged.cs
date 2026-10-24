namespace Assembly.Engine.Events;

/// <summary>
/// Work the agent left running in the background (a shell command, say) started, or reached its end.
/// <c>State</c> is <c>running</c>, <c>completed</c>, <c>failed</c> or <c>stopped</c>. <c>CanStop</c> when the agent
/// stops it on request (<c>_session/async_task/stop</c>). An untracked task is one the agent reports no end for:
/// it ends with the next <see cref="CycleEnded"/> that <c>EndsUntracked</c>.
/// </summary>
public sealed record TaskChanged(long At, string TaskId, string Name, string State, bool CanStop, string? ToolCallId, bool Tracked = true)
    : AgentEvent(At)
{
    public bool Running => State == "running";
}
