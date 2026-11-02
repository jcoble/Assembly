using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;

namespace Assembly.Infrastructure.Projects;

internal sealed class ProjectRoots(ProjectStore store) : IProjectRoots, IProjectUse
{
    public void Touched(IWriteScope scope, string projectId) => store.Touch(scope, projectId);
    public ProjectRoot? Find(string projectId) => store.Get(projectId) is { } p
        ? new(p.Id, p.Machine, p.RootPath, p.RemovedAt is not null) : null;
}
