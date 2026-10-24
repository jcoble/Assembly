namespace Assembly.Engine.Events;

/// <summary>The agent started working with no prompt from us (after background work finished, say). <c>RunId</c> names this run.</summary>
public sealed record RunStarted(long At, string RunId, string Reason) : AgentEvent(At);
