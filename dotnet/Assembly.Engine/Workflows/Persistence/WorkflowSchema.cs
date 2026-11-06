using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Persistence;

public static class WorkflowSchema
{
    public static void Upgrade(IWriteScope scope)
    {
        scope.Execute(Tables);
        Add("engine_run", "paused", "INTEGER NOT NULL DEFAULT 0 CHECK(paused IN (0,1))");
        Add("engine_run", "setup_state", "TEXT NOT NULL DEFAULT 'ready' CHECK(setup_state IN ('pending','ready','failed'))");
        Add("engine_run", "cleanup_state", "TEXT NOT NULL DEFAULT 'retained' CHECK(cleanup_state IN ('retained','checking','removed'))");
        foreach (var name in new[] { "ended_ms", "started_ms", "max_sessions", "step_timeout_ms", "max_rounds" }) Add("engine_run", name, "INTEGER NULL");
        foreach (var name in new[] { "title", "request", "project_id", "project_root", "definition_path", "definition_hash", "stage", "rerun_of", "work_head", "proof_dir", "pr_body", "cleanup_reason" })
            Add("engine_run", name, "TEXT NULL");
        foreach (var name in new[] { "definition_key", "kind", "result", "authority_machine", "authority_session_id", "dispatch_state", "label_present", "label_past", "detail", "explanation", "required_scope", "receipt_id" })
            Add("engine_step", name, "TEXT NULL");
        Add("engine_step", "workspace_id", "TEXT NULL REFERENCES engine_workflow_workspace(id)");
        foreach (var name in new[] { "generation", "round", "attempt_in_generation", "report_requested", "reminder_sent", "scope_granted", "closed" }) Add("engine_step", name, "INTEGER NOT NULL DEFAULT 0");
        foreach (var name in new[] { "deadline_ms", "authority_epoch", "progress" }) Add("engine_step", name, "INTEGER NULL");
        Add("engine_step", "badges_json", "TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(badges_json) AND json_type(badges_json)='array')");
        foreach (var name in new[] { "generation", "attempt_in_generation", "reminder_sent", "execution_epoch", "command_index", "cancel_requested" }) Add("engine_step_attempt", name, "INTEGER NOT NULL DEFAULT 0");
        foreach (var name in new[] { "provider_session_id", "fingerprint", "gate_state", "dispatch_command_id", "prompt_message_id", "rendered_prompt_sha256", "session_id", "authority_machine" })
            Add("engine_step_attempt", name, "TEXT NULL");
        Add("engine_step_attempt", "workspace_id", "TEXT NULL REFERENCES engine_workflow_workspace(id)");
        Add("engine_step_attempt", "receipt_id", "TEXT NULL REFERENCES engine_workflow_receipt(id)");
        Add("engine_step_attempt", "rendered_prompt_bytes", "INTEGER NULL");
        Add("engine_outbox", "executor_json", "TEXT NULL CHECK(executor_json IS NULL OR (json_valid(executor_json) AND json_type(executor_json)='object'))");
        Add("engine_needs_me", "source_kind", "TEXT NOT NULL DEFAULT 'provider'");
        Add("engine_needs_me", "execution_json", "TEXT NULL CHECK(execution_json IS NULL OR (json_valid(execution_json) AND json_type(execution_json)='object'))");
        scope.Execute("""
            UPDATE engine_step SET attempt_in_generation=attempt;
            UPDATE engine_step_attempt SET attempt_in_generation=attempt;
            CREATE UNIQUE INDEX engine_step_definition_key ON engine_step(run_id,definition_key) WHERE definition_key IS NOT NULL;
            CREATE INDEX engine_step_authority_state ON engine_step(authority_machine,state);
            CREATE UNIQUE INDEX engine_step_dispatch_command ON engine_step_attempt(dispatch_command_id) WHERE dispatch_command_id IS NOT NULL;
            CREATE UNIQUE INDEX engine_step_prompt_message ON engine_step_attempt(prompt_message_id) WHERE prompt_message_id IS NOT NULL;
            """);
        void Add(string table, string column, string declaration) => scope.Execute($"ALTER TABLE {table} ADD COLUMN {column} {declaration}");
    }

    private const string Tables = $$"""
        CREATE TABLE engine_workflow_snapshot ({{EngineSchema.Shared}}
            run_id TEXT NOT NULL UNIQUE REFERENCES engine_run(id), schema_version INTEGER NOT NULL,
            definition_hash TEXT NOT NULL, input_hash TEXT NOT NULL,
            snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)));
        CREATE TABLE engine_workflow_workspace ({{EngineSchema.Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), step_id TEXT NULL REFERENCES engine_step(id),
            allocation_id TEXT NOT NULL UNIQUE, path TEXT NULL, branch TEXT NULL, base_head TEXT NULL, head TEXT NULL,
            owned INTEGER NOT NULL CHECK(owned IN (0,1)), state TEXT NOT NULL, error TEXT NULL);
        CREATE INDEX engine_workflow_workspace_run_state ON engine_workflow_workspace(run_id,state);
        CREATE TABLE engine_workflow_receipt ({{EngineSchema.Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), step_id TEXT NOT NULL REFERENCES engine_step(id),
            attempt INTEGER NOT NULL, generation INTEGER NOT NULL, session_id TEXT NULL REFERENCES engine_session(id),
            epoch INTEGER NOT NULL, hash TEXT NOT NULL, kind TEXT NOT NULL,
            body_json TEXT NOT NULL CHECK(json_valid(body_json)), state TEXT NOT NULL, observed_head TEXT NULL,
            checked_ms INTEGER NULL, error TEXT NULL, UNIQUE(step_id,attempt,hash));
        CREATE INDEX engine_workflow_receipt_run_state ON engine_workflow_receipt(run_id,state);
        CREATE TABLE engine_workflow_policy ({{EngineSchema.Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), step_id TEXT NOT NULL REFERENCES engine_step(id),
            provider_request_id TEXT NULL, command_hash TEXT NOT NULL, decision TEXT NOT NULL,
            matched_rule TEXT NULL, need_id TEXT NULL REFERENCES engine_needs_me(id));
        CREATE INDEX engine_workflow_policy_step ON engine_workflow_policy(step_id);
        CREATE TABLE engine_workflow_history ({{EngineSchema.Shared}}
            run_id TEXT NOT NULL REFERENCES engine_run(id), history_seq INTEGER NOT NULL,
            step_id TEXT NULL REFERENCES engine_step(id), attempt INTEGER NULL, generation INTEGER NOT NULL,
            kind TEXT NOT NULL, at_ms INTEGER NOT NULL, safe_body_json TEXT NOT NULL CHECK(json_valid(safe_body_json)),
            UNIQUE(run_id,history_seq));
        CREATE UNIQUE INDEX engine_workflow_dispatch_intent ON engine_workflow_history(run_id,json_extract(safe_body_json,'$.commandId')) WHERE kind='dispatch_intent';
        CREATE INDEX engine_workflow_dispatch_tuple ON engine_workflow_history(run_id,step_id,generation,attempt,history_seq) WHERE kind='dispatch_intent';
        CREATE TABLE engine_workflow_start (
            owner_id TEXT NOT NULL REFERENCES engine_session(id), request_key TEXT NOT NULL,
            request_fingerprint TEXT NOT NULL, run_id TEXT NOT NULL REFERENCES engine_run(id),
            original_command_id TEXT NOT NULL REFERENCES engine_command_receipt(command_id), PRIMARY KEY(owner_id,request_key));
        CREATE INDEX engine_workflow_start_run ON engine_workflow_start(run_id);
        CREATE TABLE engine_workflow_settings (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1), muted_until INTEGER NULL,
            settings_json TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(settings_json)));
        INSERT INTO engine_workflow_settings(singleton) VALUES(1);
        """;
}
