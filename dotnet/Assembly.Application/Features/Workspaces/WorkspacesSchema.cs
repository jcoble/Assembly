namespace Assembly.Application.Features.Workspaces;

internal static class WorkspacesSchema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS workspace_snapshot(
            machine TEXT NOT NULL, session_id TEXT NOT NULL, checkout_root TEXT NOT NULL,
            schema_version INTEGER NOT NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL,
            snapshot TEXT NOT NULL CHECK(json_valid(snapshot)), PRIMARY KEY(machine,session_id,checkout_root));
        """;
}
