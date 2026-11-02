namespace Assembly.Application.Features.Projects;

public sealed record FolderInspection(string RootPath, string Title, string? RepoKey, bool IsGit, string? CommonDir, bool Unborn);
public sealed record FolderListing(string Path, IReadOnlyList<string> Directories, bool Truncated);
public sealed record GitRef(string Name, string Kind, string? CheckedOutPath);
public sealed record RefPage(IReadOnlyList<GitRef> Refs, CurrentHead Current, string? Next);
public sealed record CurrentHead(string? Branch, string? Head);
