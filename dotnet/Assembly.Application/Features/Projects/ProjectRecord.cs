namespace Assembly.Application.Features.Projects;

public sealed record ProjectRecord(string Id, string Machine, string RootPath, string Title, string? RepoKey,
    long? PinnedAt, long? RemovedAt, long CreatedAt, long? LastUsedAt);
public sealed record ProjectsRevision(long Revision);
public sealed record ProjectCursor(int Pinned, long? LastUsedAt, long CreatedAt, string Id);
public sealed record ProjectPage(IReadOnlyList<ProjectRecord> Projects, ProjectCursor? Next);
