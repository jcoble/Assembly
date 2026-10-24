namespace Assembly.Engine.Events;

/// <summary>The turn's token counts, as the agent reports them.</summary>
public sealed record TokenUsage(long? InputTokens, long? OutputTokens, long? CachedReadTokens, long? CachedWriteTokens);
