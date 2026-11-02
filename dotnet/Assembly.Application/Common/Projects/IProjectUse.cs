using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Projects;

/// <summary>Registry last-use projection in the owning start transaction.</summary>
public interface IProjectUse { void Touched(IWriteScope scope, string projectId); }
