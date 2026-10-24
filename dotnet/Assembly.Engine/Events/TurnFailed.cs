namespace Assembly.Engine.Events;

/// <summary>The agent answered the prompt with an error.</summary>
public sealed record TurnFailed(long At, string Message, string? Code) : AgentEvent(At);
