namespace Assembly.Application.Common.Projects;

public sealed record ProjectRoot(string Id, string Machine, string RootPath, bool Removed);
public interface IProjectRoots { ProjectRoot? Find(string projectId); }
