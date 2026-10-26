namespace Assembly.Domain.Events;

/// <summary>
/// What the agent says the turn has cost so far, and which model it used, when it says. <c>Used</c> and <c>Size</c>
/// are how full its context window is, in tokens, and how big it is.
/// </summary>
public sealed record UsageReported(long At, decimal? CostUsd, string? Model, long? Used = null, long? Size = null) : AgentEvent(At);
