using Assembly.Application.Common.Database;

namespace Assembly.Engine.Persistence;

public static class PrSchemaUpgrade
{
    public static void Apply(IWriteScope scope)
    {
        // Keep engine_pr itself in place: engine_run.pr_id references its retained identities.
        scope.Execute("""
            ALTER TABLE engine_pr ADD COLUMN run_id_nullable TEXT REFERENCES engine_run(id);
            UPDATE engine_pr SET run_id_nullable=run_id;
            ALTER TABLE engine_pr ADD COLUMN owner_session_id TEXT REFERENCES engine_session(id)
                CHECK(run_id_nullable IS NOT NULL OR owner_session_id IS NOT NULL);
            ALTER TABLE engine_pr DROP COLUMN run_id;
            ALTER TABLE engine_pr RENAME COLUMN run_id_nullable TO run_id;
            CREATE INDEX engine_pr_run ON engine_pr(run_id);
            CREATE INDEX engine_pr_owner ON engine_pr(owner_session_id);
            """);
        scope.Execute($"""
            CREATE TABLE engine_pr_observation ({EngineSchema.Shared}
                origin_machine TEXT NOT NULL CHECK(origin_machine=machine), coordinator_machine TEXT NOT NULL,
                session_id TEXT NOT NULL, run_id TEXT, step_id TEXT, epoch INTEGER NOT NULL CHECK(epoch>=0),
                facts_json TEXT NOT NULL CHECK(json_valid(facts_json) AND length(CAST(facts_json AS BLOB))<=24576),
                state TEXT NOT NULL CHECK(state IN ('pending','recorded')), coordinator_seq INTEGER,
                CHECK((state='pending' AND coordinator_seq IS NULL) OR (state='recorded' AND coordinator_seq>0)));
            CREATE INDEX engine_pr_observation_pending ON engine_pr_observation(state,created_ms,id);
            UPDATE engine_meta SET schema_version=13 WHERE singleton=1;
            """);
    }
}
