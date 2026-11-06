namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>A session's token total over its turns (<c>rows/tokens</c>), null when none reported one.</summary>
public sealed record SessionTokens(long? Total);
