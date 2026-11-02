using Assembly.Application.Common.Database;

namespace Assembly.Engine.Persistence;

internal static class SessionProjectSchemaUpgrade
{
    internal static void Apply(IWriteScope scope)
    {
        Add("project_id", "TEXT NULL");
        Add("branch", "TEXT NULL");
        Add("checkout_state", "TEXT NOT NULL DEFAULT 'ready' CHECK(checkout_state IN ('ready','pending','refused'))");
        Add("checkout_operation_id", "TEXT NULL");
        Add("checkout_intent_json", "TEXT NULL CHECK(checkout_intent_json IS NULL OR json_valid(checkout_intent_json))");
        void Add(string column, string declaration)
        {
            if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('engine_session') WHERE name=$name", r => r.Long(0), new SqlArg("$name", column)) == 0)
                scope.Execute($"ALTER TABLE engine_session ADD COLUMN {column} {declaration}");
        }
    }
}
