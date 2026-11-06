namespace Assembly.Infrastructure.Chat.Files;

/// <summary>One bounded directory page, with a generation-bound continuation when more entries remain.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, bool Truncated, PageCursor? NextCursor = null);
