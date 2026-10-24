namespace Assembly.Engine.Events;

/// <summary>
/// The agent finished a stretch of work: the end of a prompted turn, or of one it started itself.
/// <c>Origin</c> is why that work started, when the agent says (<c>human</c>, <c>task-notification</c>).
/// <c>EndsUntracked</c> when it was the agent's answer to its untracked background work finishing.
/// </summary>
public sealed record CycleEnded(long At, string? Origin, bool EndsUntracked = false) : AgentEvent(At);
