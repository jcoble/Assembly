using Assembly.Application.Common.Projects;
namespace Assembly.Application.Features.Projects;

public sealed record ProjectFolderCreate(string ParentPath, string Name, string Kind, string? Source);
public sealed record NewProjectCreate(string Machine, string ParentPath, string Name, string Kind, string? Source);
public sealed record NewProjectResult(ProjectRecord Project, FolderInspection Inspection, bool AlreadyExisted);
