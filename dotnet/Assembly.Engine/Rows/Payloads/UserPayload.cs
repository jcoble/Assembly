namespace Assembly.Engine.Rows.Payloads;

/// <summary>A prompt: a person's, or a message from another session's thread (<c>From</c>).</summary>
public sealed record UserPayload(string Text, string? From = null);
