using Assembly.Application.Common.Database;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Persistence;

public sealed record EngineSchemaInitialization(string Machine, long? PreviousVersion);

public static class EngineSchema
{
    public static string Open(IAppDatabase db, long nowMs, Action<IWriteScope>? projectionSchema = null, int expiryMinutes = 30) =>
        db.Writer.Write("engine.schema", scope => Initialize(scope, nowMs, projectionSchema, expiryMinutes).Machine);

    public static EngineSchemaInitialization Initialize(IWriteScope scope, long nowMs, Action<IWriteScope>? projectionSchema = null, int expiryMinutes = 30)
    {
        if (expiryMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(expiryMinutes));
        scope.Execute("""
            CREATE TABLE IF NOT EXISTS engine_meta (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1), schema_version INTEGER NOT NULL,
                machine_id TEXT NOT NULL, next_seq INTEGER NOT NULL, retained_after INTEGER NOT NULL,
                snapshot_seq INTEGER NOT NULL);
            """);
        var meta = scope.QueryFirst("SELECT schema_version,machine_id FROM engine_meta WHERE singleton=1",
            r => (Version: r.Long(0), Machine: r.Text(1)));
        if (meta.Machine is null)
        {
            scope.Execute(Create);
            scope.Execute(ProviderSchema.Create);
            scope.Execute(CreateMirrors);
            projectionSchema?.Invoke(scope);
            var machine = Guid.NewGuid().ToString("N");
            scope.Execute("""
                INSERT INTO engine_meta(singleton,schema_version,machine_id,next_seq,retained_after,snapshot_seq)
                VALUES(1,9,$machine,1,0,0);
                """, new SqlArg("$machine", machine));
            return new(machine, null);
        }
        if (meta.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9))
            throw new InvalidOperationException($"Unsupported engine schema version {meta.Version}.");
        if (meta.Version is 6 or 7 or 8 or 9)
        {
            projectionSchema?.Invoke(scope);
            if (meta.Version == 6) NeedsMe.RoutedNeedSchemaUpgrade.Apply(scope);
            if (meta.Version < 8) AuthSchemaUpgrade.Apply(scope);
            AccountIdentitySchema.Validate(scope, meta.Version < 9);
            if (meta.Version < 9) scope.Execute("UPDATE engine_meta SET schema_version=9 WHERE singleton=1");
            return new(meta.Machine, meta.Version);
        }
        if (meta.Version == 1)
        {
            scope.Execute(UpgradeVersion1);
            scope.Execute(CreateMirrors);
        }
        if (meta.Version < 3)
        {
            scope.Execute(UpgradeDelivery);
            Messages.MessageSchemaUpgrade.Apply(scope, meta.Machine, nowMs);
        }
        if (meta.Version < 4) SessionSchemaUpgrade.Apply(scope, projectionSchema);
        else projectionSchema?.Invoke(scope);
        if (meta.Version < 5) ComposerSchemaUpgrade.Apply(scope);
        MessageExpirySchemaUpgrade.Apply(scope, expiryMinutes);
        NeedsMe.RoutedNeedSchemaUpgrade.Apply(scope);
        AuthSchemaUpgrade.Apply(scope);
        AccountIdentitySchema.Validate(scope, true);
        scope.Execute("UPDATE engine_meta SET schema_version=9 WHERE singleton=1");
        return new(meta.Machine, meta.Version);
    }

    private const string UpgradeDelivery = """
        ALTER TABLE engine_message ADD COLUMN delivery TEXT NOT NULL DEFAULT 'wait';
        ALTER TABLE engine_message ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE engine_message ADD COLUMN in_flight INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE engine_message ADD COLUMN sender_label TEXT NOT NULL DEFAULT '';
        ALTER TABLE engine_session ADD COLUMN can_steer INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE engine_message ADD COLUMN client_id TEXT NULL;
        """;

    internal const string Shared = """
        id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
        """;

    private const string EventColumns = $"""
        {Shared}
        seq INTEGER NOT NULL, kind TEXT NOT NULL, entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
        entity_version INTEGER NOT NULL, at_ms INTEGER NOT NULL, late INTEGER NOT NULL,
        data_json TEXT NOT NULL CHECK(json_valid(data_json)), UNIQUE(machine,seq)
        """;

    private const string UpgradeVersion1 = $"""
        CREATE TABLE engine_event_v2 ({EventColumns});
        INSERT INTO engine_event_v2(id,version,machine,created_ms,updated_ms,task_url,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
        SELECT id,version,machine,created_ms,updated_ms,task_url,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json FROM engine_event;
        DROP TABLE engine_event;
        ALTER TABLE engine_event_v2 RENAME TO engine_event;
        CREATE INDEX engine_event_machine_seq ON engine_event(machine,seq);
        """;

    private const string CreateMirrors = """
        CREATE TABLE engine_mirror (
            machine TEXT PRIMARY KEY, name TEXT NOT NULL, generation INTEGER NOT NULL, through INTEGER NOT NULL,
            at_json TEXT CHECK(at_json IS NULL OR json_valid(at_json)), retained_after INTEGER NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('snapshotting','live','retired')), updated_ms INTEGER NOT NULL);
        CREATE TABLE engine_mirror_row (
            machine TEXT NOT NULL REFERENCES engine_mirror(machine), entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
            generation INTEGER NOT NULL, version INTEGER NOT NULL, row_json TEXT NOT NULL CHECK(json_valid(row_json)),
            PRIMARY KEY(machine,generation,entity_kind,entity_id));
        """;

    private const string Create = $$"""
        CREATE TABLE engine_session ({{Shared}}
            provider TEXT NOT NULL, model TEXT NULL, folder TEXT NOT NULL, title TEXT NOT NULL,
            account_context TEXT NOT NULL DEFAULT '',
            managed_checkout INTEGER NOT NULL DEFAULT 0 CHECK(managed_checkout IN (0,1)),
            state TEXT NOT NULL, activity_state TEXT NOT NULL,
            parent_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            provider_session_id TEXT, holder TEXT NOT NULL, epoch INTEGER NOT NULL,
            reserved_epoch INTEGER, cancel_requested INTEGER NOT NULL, lost_ms INTEGER, can_steer INTEGER NOT NULL DEFAULT 0);
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
            addressee_session_id TEXT NULL REFERENCES engine_session(id),
            answer_json TEXT CHECK(answer_json IS NULL OR json_valid(answer_json)), answered_by TEXT, answered_ms INTEGER);
        CREATE TABLE engine_message ({{Shared}}
            from_session_id TEXT REFERENCES engine_session(id), sender_kind TEXT NOT NULL,
            to_session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), text TEXT NOT NULL, mode TEXT NOT NULL, state TEXT NOT NULL,
            provider_turn_id TEXT, reason TEXT, delivery TEXT NOT NULL DEFAULT 'wait',
            attempt INTEGER NOT NULL DEFAULT 0, in_flight INTEGER NOT NULL DEFAULT 0,
            sender_label TEXT NOT NULL DEFAULT '', client_id TEXT NULL,
            expires_ms INTEGER NULL,
            framed INTEGER NOT NULL DEFAULT 1 CHECK(framed IN (0,1)),
            attachment_ids_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(attachment_ids_json) AND json_type(attachment_ids_json)='array'),
            held_epoch INTEGER NULL, target_epoch INTEGER NULL CHECK(target_epoch IS NULL OR target_epoch>=0),
            owner_content_sha256 TEXT NULL CHECK(owner_content_sha256 IS NULL OR
                (length(owner_content_sha256)=64 AND owner_content_sha256 NOT GLOB '*[^0-9a-f]*')));
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
        CREATE TABLE engine_event ({{EventColumns}});
        CREATE INDEX engine_event_machine_seq ON engine_event(machine,seq);
        -- event_seq is provenance: events may be compacted while an effect remains durable.
        CREATE TABLE engine_outbox ({{Shared}}
            event_seq INTEGER NOT NULL, session_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            kind TEXT NOT NULL, payload_json TEXT NOT NULL CHECK(json_valid(payload_json)), state TEXT NOT NULL,
            attempt INTEGER NOT NULL, next_ms INTEGER, receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json)),
            last_error TEXT, order_key TEXT NOT NULL DEFAULT '');
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
        {{MessageExpirySchemaUpgrade.Index}}
        CREATE INDEX engine_message_recipient_state ON engine_message(to_session_id,state);
        CREATE INDEX engine_needs_me_state_deadline ON engine_needs_me(state,deadline_ms);
        CREATE UNIQUE INDEX engine_needs_me_provider_request ON engine_needs_me(session_id,provider_request_id)
            WHERE provider_request_id IS NOT NULL;
        CREATE INDEX engine_outbox_state_next ON engine_outbox(state,next_ms);
        CREATE INDEX engine_outbox_session_order ON engine_outbox(session_id,event_seq);
        {{AuthSchemaUpgrade.OrderIndex}}
        CREATE INDEX engine_tool_call_session_time ON engine_tool_call(session_id,started_ms);
        CREATE INDEX engine_tool_call_run_time ON engine_tool_call(run_id,started_ms);
        CREATE INDEX engine_tool_call_step_attempt_time ON engine_tool_call(step_id,attempt,started_ms);
        CREATE INDEX engine_usage_session_time ON engine_usage(session_id,created_ms);
        CREATE INDEX engine_usage_run_time ON engine_usage(run_id,created_ms);
        CREATE INDEX engine_usage_step_attempt_time ON engine_usage(step_id,attempt,created_ms);
        """;
}
