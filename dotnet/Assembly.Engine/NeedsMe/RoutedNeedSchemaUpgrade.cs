using Assembly.Application.Common.Database;

namespace Assembly.Engine.NeedsMe;

internal static class RoutedNeedSchemaUpgrade
{
    internal static void Apply(IWriteScope scope)
    {
        scope.Execute("""
            ALTER TABLE engine_needs_me
            ADD COLUMN addressee_session_id TEXT NULL REFERENCES engine_session(id);
            UPDATE engine_meta SET schema_version=7 WHERE singleton=1;
            """);
    }
}
