using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Engine.Persistence;

public static class EngineSchema
{
    public static string Open(IAppDatabase db, long nowMs) => db.Writer.Write("engine.schema", scope =>
    {
        scope.Execute("""
            CREATE TABLE IF NOT EXISTS engine_meta (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1), schema_version INTEGER NOT NULL,
                machine_id TEXT NOT NULL, next_seq INTEGER NOT NULL, retained_after INTEGER NOT NULL,
                snapshot_seq INTEGER NOT NULL);
            """);
        var meta = scope.QueryFirst("SELECT schema_version,machine_id FROM engine_meta WHERE singleton=1",
            r => (Version: r.Long(0), Machine: r.Text(1)));
        if (meta.Version > 1)
            throw new InvalidOperationException($"Unsupported engine schema version {meta.Version}.");
        if (meta.Version == 1)
            return meta.Machine;

        scope.Execute(Create);
        var machine = Guid.NewGuid().ToString("N");
        scope.Execute("""
            INSERT INTO engine_meta(singleton,schema_version,machine_id,next_seq,retained_after,snapshot_seq)
            VALUES(1,1,$machine,1,0,0);
            """, new SqlArg("$machine", machine));
        return machine;
    });

    private const string Shared = """
        id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
        """;

    private const string Create = $$"""
        CREATE TABLE engine_session ({{Shared}}
            provider TEXT NOT NULL, model TEXT NOT NULL, folder TEXT NOT NULL, title TEXT NOT NULL,
            state TEXT NOT NULL, activity_state TEXT NOT NULL,
            parent_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            provider_session_id TEXT, holder TEXT NOT NULL, epoch INTEGER NOT NULL,
            reserved_epoch INTEGER, cancel_requested INTEGER NOT NULL, lost_ms INTEGER);
        CREATE TABLE engine_run ({{Shared}}
            owner_session_id TEXT NOT NULL REFERENCES engine_session(id), worktree TEXT, branch TEXT,
            pr_id TEXT REFERENCES engine_pr(id), state TEXT NOT NULL, owner_loss_limit_ms INTEGER NOT NULL,
            owner_lost_ms INTEGER);
        CREATE TABLE engine_step ({{Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), name TEXT NOT NULL,
            session_id TEXT REFERENCES engine_session(id), state TEXT NOT NULL, attempt INTEGER NOT NULL,
            max_attempts INTEGER NOT NULL, required INTEGER NOT NULL, rule_skippable INTEGER NOT NULL,
            started_ms INTEGER, ended_ms INTEGER, proof_json TEXT CHECK(proof_json IS NULL OR json_valid(proof_json)),
            outcome_reason TEXT);
        CREATE TABLE engine_needs_me ({{Shared}}
            session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), kind TEXT NOT NULL, text TEXT NOT NULL,
            options_json TEXT NOT NULL CHECK(json_valid(options_json)), provider_request_id TEXT, chat_row_id TEXT,
            state TEXT NOT NULL, deadline_ms INTEGER,
            answer_json TEXT CHECK(answer_json IS NULL OR json_valid(answer_json)), answered_by TEXT, answered_ms INTEGER);
        CREATE TABLE engine_message ({{Shared}}
            from_session_id TEXT REFERENCES engine_session(id), sender_kind TEXT NOT NULL,
            to_session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), text TEXT NOT NULL, mode TEXT NOT NULL, state TEXT NOT NULL,
            provider_turn_id TEXT, reason TEXT);
        CREATE TABLE engine_handoff ({{Shared}}
            session_id TEXT NOT NULL REFERENCES engine_session(id), old_holder TEXT NOT NULL, new_holder TEXT NOT NULL,
            old_epoch INTEGER NOT NULL, next_epoch INTEGER NOT NULL, state TEXT NOT NULL, reason TEXT);
        CREATE TABLE engine_pr ({{Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), repo TEXT NOT NULL, number INTEGER, head TEXT,
            state TEXT NOT NULL, reported_by TEXT, reported_state TEXT, observed_head TEXT, observed_ms INTEGER, url TEXT);
        CREATE TABLE engine_tool_call ({{Shared}}
            session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), attempt INTEGER, epoch INTEGER NOT NULL, boundary TEXT NOT NULL,
            boundary_id TEXT NOT NULL, tool TEXT NOT NULL, input_preview TEXT, output_preview TEXT,
            input_bytes INTEGER NOT NULL, output_bytes INTEGER, started_ms INTEGER NOT NULL, ended_ms INTEGER,
            outcome TEXT, late INTEGER NOT NULL, UNIQUE(boundary,boundary_id));
        CREATE TABLE engine_usage ({{Shared}}
            session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), attempt INTEGER, epoch INTEGER NOT NULL, provider_turn_id TEXT NOT NULL,
            input_tokens INTEGER, output_tokens INTEGER, cached_tokens INTEGER, cost_nanodollars INTEGER,
            duration_ms INTEGER, late INTEGER NOT NULL, UNIQUE(session_id,provider_turn_id));
        CREATE TABLE engine_event ({{Shared}}
            seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
            entity_version INTEGER NOT NULL, at_ms INTEGER NOT NULL, late INTEGER NOT NULL,
            data_json TEXT NOT NULL CHECK(json_valid(data_json)));
        -- event_seq is provenance: events may be compacted while an effect remains durable.
        CREATE TABLE engine_outbox ({{Shared}}
            event_seq INTEGER NOT NULL, session_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            kind TEXT NOT NULL, payload_json TEXT NOT NULL CHECK(json_valid(payload_json)), state TEXT NOT NULL,
            attempt INTEGER NOT NULL, next_ms INTEGER, receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json)),
            last_error TEXT);
        CREATE TABLE engine_command_receipt (
            command_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, result_json TEXT NOT NULL CHECK(json_valid(result_json)));
        CREATE TABLE engine_step_dependency (
            step_id TEXT NOT NULL REFERENCES engine_step(id), dependency_id TEXT NOT NULL REFERENCES engine_step(id),
            optional INTEGER NOT NULL, PRIMARY KEY(step_id,dependency_id));
        CREATE TABLE engine_step_attempt (
            step_id TEXT NOT NULL REFERENCES engine_step(id), attempt INTEGER NOT NULL, state TEXT NOT NULL,
            started_ms INTEGER, ended_ms INTEGER, proof_json TEXT CHECK(proof_json IS NULL OR json_valid(proof_json)),
            reason TEXT, late INTEGER NOT NULL, PRIMARY KEY(step_id,attempt));
        CREATE TABLE engine_step_fact (
            id TEXT PRIMARY KEY, step_id TEXT NOT NULL REFERENCES engine_step(id), attempt INTEGER NOT NULL,
            kind TEXT NOT NULL, at_ms INTEGER NOT NULL, data_json TEXT NOT NULL CHECK(json_valid(data_json)), late INTEGER NOT NULL);
        CREATE INDEX engine_session_parent_state ON engine_session(parent_id,state);
        CREATE INDEX engine_session_run_state ON engine_session(run_id,state);
        CREATE INDEX engine_step_run_state ON engine_step(run_id,state);
        CREATE INDEX engine_message_recipient_state ON engine_message(to_session_id,state);
        CREATE INDEX engine_needs_me_state_deadline ON engine_needs_me(state,deadline_ms);
        CREATE UNIQUE INDEX engine_needs_me_provider_request ON engine_needs_me(session_id,provider_request_id)
            WHERE provider_request_id IS NOT NULL;
        CREATE INDEX engine_outbox_state_next ON engine_outbox(state,next_ms);
        CREATE INDEX engine_outbox_session_order ON engine_outbox(session_id,event_seq);
        CREATE INDEX engine_tool_call_session_time ON engine_tool_call(session_id,started_ms);
        CREATE INDEX engine_tool_call_run_time ON engine_tool_call(run_id,started_ms);
        CREATE INDEX engine_tool_call_step_attempt_time ON engine_tool_call(step_id,attempt,started_ms);
        CREATE INDEX engine_usage_session_time ON engine_usage(session_id,created_ms);
        CREATE INDEX engine_usage_run_time ON engine_usage(run_id,created_ms);
        CREATE INDEX engine_usage_step_attempt_time ON engine_usage(step_id,attempt,created_ms);
        """;
}
