namespace Assembly.Application.Common.Acp.Messages;

/// <summary>The token counts on a prompt's result (ACP's usage extension).</summary>
public sealed record PromptUsage(long? InputTokens, long? OutputTokens, long? CachedReadTokens, long? CachedWriteTokens);
