namespace Assembly.Domain.Events;

/// <summary>The turn's token counts, as the agent reports them. <c>TotalTokens</c> is the turn's own total; it restarts each turn.</summary>
public sealed record TokenUsage(long? InputTokens, long? OutputTokens, long? CachedReadTokens, long? CachedWriteTokens,
    long? TotalTokens = null);
