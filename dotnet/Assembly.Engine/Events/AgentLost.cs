namespace Assembly.Engine.Events;

/// <summary>The agent's process went away during a turn.</summary>
public sealed record AgentLost(long At) : AgentEvent(At);
