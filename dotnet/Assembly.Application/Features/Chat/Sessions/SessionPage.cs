using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// Sessions, most recently used first. <c>NextBefore</c> and <c>NextBeforeId</c> are the next page's cursor, or null at
/// the end.
/// </summary>
public sealed record SessionPage(IReadOnlyList<SessionInfo> Sessions, long? NextBefore, string? NextBeforeId);
