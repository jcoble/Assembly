using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal static class ProviderSchema
{
    internal const string Create = $"""
        CREATE TABLE engine_provider ({EngineSchema.Shared}
            provider TEXT NOT NULL, account_context TEXT NOT NULL,
            installation TEXT NOT NULL, auth_state TEXT NOT NULL, reason TEXT NULL,
            facts_json TEXT NULL CHECK(facts_json IS NULL OR json_valid(facts_json)), checked_ms INTEGER NULL);
        CREATE TABLE engine_sign_in ({EngineSchema.Shared}
            provider TEXT NOT NULL, account_context TEXT NOT NULL, method_id TEXT NULL,
            state TEXT NOT NULL, reason TEXT NULL, deadline_ms INTEGER NOT NULL,
            origin_need_id TEXT NULL REFERENCES engine_needs_me(id));
        CREATE UNIQUE INDEX engine_sign_in_active_target ON engine_sign_in(machine,provider,account_context)
            WHERE state IN ('starting','waiting_for_person','finishing');
        CREATE INDEX engine_sign_in_active_deadline ON engine_sign_in(deadline_ms,id)
            WHERE state IN ('starting','waiting_for_person','finishing');
        """;
}
