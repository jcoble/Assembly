namespace Assembly.Domain.Rows;

/// <summary>A prompt: a person's, or a message from another session (<c>From</c>).</summary>
public sealed record UserPayload(string Text, string? From = null);
