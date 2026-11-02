using Assembly.Application.Common.Database;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Persistence;

internal static class AuthSchemaUpgrade
{
    internal const string OrderIndex = "CREATE INDEX engine_outbox_order ON engine_outbox(order_key,event_seq);";

    internal static void Apply(IWriteScope scope)
    {
        scope.Execute(ProviderSchema.Create);
        scope.Execute("""
            ALTER TABLE engine_outbox ADD COLUMN order_key TEXT NOT NULL DEFAULT '';
            UPDATE engine_outbox SET order_key=COALESCE(session_id,'effect/'||id);
            """);
        scope.Execute(OrderIndex);
        scope.Execute("UPDATE engine_meta SET schema_version=8 WHERE singleton=1");
    }
}
