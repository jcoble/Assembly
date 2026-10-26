using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>Threads, most recently used first. <c>NextBefore</c> is the next page's <c>before</c>, or null at the end.</summary>
public sealed record SessionPage(IReadOnlyList<SessionInfo> Sessions, long? NextBefore);
