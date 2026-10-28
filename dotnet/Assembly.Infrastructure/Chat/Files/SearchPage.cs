namespace Assembly.Infrastructure.Chat.Files;

/// <summary>A page of a name search. <c>NextCursor</c> asks for the next page; null once <c>Complete</c>.</summary>
public sealed record SearchPage(IReadOnlyList<NameMatch> Matches, int? NextCursor, bool Complete);
