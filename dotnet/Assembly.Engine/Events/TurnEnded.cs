namespace Assembly.Engine.Events;

/// <summary>
/// The agent answered the prompt. <c>StopReason</c> is <c>end_turn</c>, <c>max_tokens</c>,
/// <c>max_turn_requests</c>, <c>refusal</c> or <c>cancelled</c>.
/// </summary>
public sealed record TurnEnded(long At, string StopReason, TokenUsage? Usage) : AgentEvent(At);
