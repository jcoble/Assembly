namespace Assembly.Infrastructure.Chat.Files;

/// <summary>One entry of a listed folder. <c>Path</c> is from the session's folder, with '/' between names; a link to a folder is a folder, though listing it is refused.</summary>
public sealed record FileEntry(string Path, string Name, bool IsDirectory, bool Excluded);
