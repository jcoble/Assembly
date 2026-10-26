namespace Assembly.Domain.Rows;

/// <summary>
/// <c>State</c> is <c>started</c>, <c>finished</c>, <c>failed</c> or <c>cancelled</c>. <c>Reason</c> says why a run
/// the agent started itself started.
/// </summary>
public sealed record TurnPayload(string State, string? Reason = null);
