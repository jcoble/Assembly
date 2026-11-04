using Assembly.Application.Common.Database;
namespace Assembly.Engine.Workflows.Persistence;

internal static class WorkflowControlSchema
{
    internal static void Upgrade(IWriteScope s) => s.Execute("""
        ALTER TABLE engine_workflow_settings ADD COLUMN version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE engine_run ADD COLUMN keep_token TEXT NULL;
        ALTER TABLE engine_run ADD COLUMN keep_until_ms INTEGER NULL;
        ALTER TABLE engine_run ADD COLUMN keep_version INTEGER NULL;
        UPDATE engine_meta SET schema_version=15 WHERE singleton=1;
        """);
}
