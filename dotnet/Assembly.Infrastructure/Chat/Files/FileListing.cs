namespace Assembly.Infrastructure.Chat.Files;

/// <summary>One folder's entries; <c>Truncated</c> when it held more than <see cref="DirectoryListing.Limit"/>.</summary>
public sealed record FileListing(IReadOnlyList<FileEntry> Entries, bool Truncated);
