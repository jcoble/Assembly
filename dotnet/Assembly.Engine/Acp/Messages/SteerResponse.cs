namespace Assembly.Engine.Acp.Messages;

/// <summary><c>injected</c> into the running turn, <c>promptRequired</c> when no turn was running, or <c>failed</c>.</summary>
public sealed record SteerResponse(string Outcome, string? Reason);
