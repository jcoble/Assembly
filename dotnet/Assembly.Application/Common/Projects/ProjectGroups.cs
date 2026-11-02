namespace Assembly.Application.Common.Projects;

public static class ProjectGroups
{
    public const string Join = "LEFT JOIN projects pr ON pr.id = s.project_id";
    public const string Key = "CASE WHEN pr.repo_key IS NOT NULL THEN 'repo:' || lower(pr.repo_key) WHEN pr.id IS NOT NULL THEN 'project:' || pr.id ELSE 'none' END";
}
