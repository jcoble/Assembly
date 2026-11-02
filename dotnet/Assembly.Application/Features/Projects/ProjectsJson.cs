using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Projects;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectListArgs))]
[JsonSerializable(typeof(ProjectPathArgs))]
[JsonSerializable(typeof(ProjectIdArgs))]
[JsonSerializable(typeof(ProjectRefsArgs))]
[JsonSerializable(typeof(ProjectRenameArgs))]
[JsonSerializable(typeof(ProjectPinArgs))]
[JsonSerializable(typeof(ProjectSetRootArgs))]
[JsonSerializable(typeof(ProjectRemoveArgs))]
[JsonSerializable(typeof(ProjectResult))]
[JsonSerializable(typeof(ProjectAddResult))]
[JsonSerializable(typeof(ProjectSetRootResult))]
[JsonSerializable(typeof(ProjectPage))]
[JsonSerializable(typeof(ProjectFolderCreate))]
[JsonSerializable(typeof(NewProjectCreate))]
[JsonSerializable(typeof(NewProjectResult))]
[JsonSerializable(typeof(ProjectsRevision))]
[JsonSerializable(typeof(FolderPathArgs))]
[JsonSerializable(typeof(FolderRootArgs))]
[JsonSerializable(typeof(FolderRefsArgs))]
[JsonSerializable(typeof(FolderWorktrees))]
[JsonSerializable(typeof(FolderInspection))]
[JsonSerializable(typeof(FolderListing))]
[JsonSerializable(typeof(RefPage))]
internal sealed partial class ProjectsJson : JsonSerializerContext;
