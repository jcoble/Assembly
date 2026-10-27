namespace Assembly.Domain.Events;

/// <summary>We sent the agent a prompt. <c>RunId</c> names this turn; <c>ClientId</c> is the sender's id for the prompt.</summary>
public sealed record TurnStarted(long At, string RunId, string Text, string? ClientId = null) : AgentEvent(At);
