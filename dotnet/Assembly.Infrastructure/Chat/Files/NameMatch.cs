namespace Assembly.Infrastructure.Chat.Files;

/// <summary>A file or folder whose name matched a search. <c>Path</c> and <c>RelativePath</c> are both from the session's folder.</summary>
public sealed record NameMatch(string Path, string Name, bool IsDirectory, string RelativePath);
