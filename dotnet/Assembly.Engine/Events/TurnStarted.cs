namespace Assembly.Engine.Events;

/// <summary>We sent the agent a prompt. <c>RunId</c> names this turn.</summary>
public sealed record TurnStarted(long At, string RunId, string Text) : AgentEvent(At);
