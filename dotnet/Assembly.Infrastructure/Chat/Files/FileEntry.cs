namespace Assembly.Infrastructure.Chat.Files;

/// <summary>One entry of a listed folder. <c>Path</c> is from the session's folder, with '/' between names; a link is never a folder.</summary>
public sealed record FileEntry(string Path, string Name, bool IsDirectory, bool Excluded);
