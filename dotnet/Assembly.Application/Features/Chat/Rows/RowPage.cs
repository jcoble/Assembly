namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// One page of a conversation, oldest first. <c>Before</c> is the lowest first seq on the page,
/// which is the next page's <c>before</c>.
/// </summary>
public sealed record RowPage(IReadOnlyList<StoredRow> Rows, bool HasBefore, long? Before);
