namespace Assembly.Application.Features.Projects;

public static class ProjectsSchema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS projects(
            id TEXT PRIMARY KEY, machine TEXT NOT NULL, root_path TEXT NOT NULL, title TEXT NOT NULL,
            repo_key TEXT, pinned_at INTEGER, removed_at INTEGER, created_at INTEGER NOT NULL,
            last_used_at INTEGER, UNIQUE(machine, root_path));
        CREATE INDEX IF NOT EXISTS projects_order ON projects(pinned_at IS NULL, last_used_at DESC, created_at, id);
        CREATE TABLE IF NOT EXISTS projects_meta(revision INTEGER NOT NULL);
        INSERT INTO projects_meta(revision) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM projects_meta);
        """;
}
