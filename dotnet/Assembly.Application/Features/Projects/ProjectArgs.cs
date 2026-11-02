using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Projects;

internal sealed record ProjectListArgs(string? Filter = null, string? Machine = null, ProjectCursor? After = null, int Limit = 100)
{
    public int Limit { get; init; } = ProjectArguments.Limit(Limit);
}
internal sealed record ProjectPathArgs(string Machine, string Path)
{
    [JsonRequired]
    public string Machine { get; init; } = ProjectArguments.Required(Machine, "machine");
    [JsonRequired]
    public string Path { get; init; } = ProjectArguments.Required(Path, "path");
}
internal sealed record ProjectIdArgs(string ProjectId)
{
    [JsonRequired]
    public string ProjectId { get; init; } = ProjectArguments.Required(ProjectId, "projectId");
}
internal sealed record ProjectRefsArgs([property: JsonRequired] string ProjectId, string? CheckoutPath = null,
    string? Filter = null, string? After = null, int Limit = 100)
{
    public int Limit { get; init; } = ProjectArguments.Limit(Limit);
}
internal sealed record ProjectRenameArgs([property: JsonRequired] string Id, string Title)
{
    [JsonRequired]
    public string Title { get; init; } = string.IsNullOrWhiteSpace(Title) ? throw new JsonException("A project title is required.") : Title;
}
internal sealed record ProjectPinArgs([property: JsonRequired] string Id, [property: JsonRequired] bool Pinned);
internal sealed record ProjectSetRootArgs([property: JsonRequired] string Id, [property: JsonRequired] string Path);
internal sealed record ProjectRemoveArgs([property: JsonRequired] string Id, bool OnlyIfEmpty = false);
internal sealed record ProjectResult(ProjectRecord Project);
internal sealed record ProjectAddResult(ProjectRecord Project, FolderInspection Inspection, bool AlreadyExisted);
internal sealed record ProjectSetRootResult(ProjectRecord Project, FolderInspection Inspection);
internal sealed record FolderPathArgs([property: JsonRequired] string Path);
internal sealed record FolderRootArgs([property: JsonRequired] string Root);
internal sealed record FolderRefsArgs([property: JsonRequired] string Root, string? CheckoutPath = null,
    string? Filter = null, string? After = null, int Limit = 100)
{
    public int Limit { get; init; } = ProjectArguments.Limit(Limit);
}
internal sealed record FolderWorktrees(IReadOnlyList<Assembly.Application.Common.Git.Worktree> Worktrees);
internal static class ProjectArguments
{
    internal static string Required(string value, string field) => value ?? throw new JsonException($"{field} is required.");
    internal static int Limit(int value) => value is >= 1 and <= 100 ? value : throw new JsonException("limit must be between 1 and 100.");
}
