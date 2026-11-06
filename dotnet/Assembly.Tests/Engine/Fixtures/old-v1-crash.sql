PRAGMA defer_foreign_keys=ON;
CREATE TABLE engine_command_receipt (
    command_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, result_json TEXT NOT NULL CHECK(json_valid(result_json)));
CREATE TABLE engine_event (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
    entity_version INTEGER NOT NULL, at_ms INTEGER NOT NULL, late INTEGER NOT NULL,
    data_json TEXT NOT NULL CHECK(json_valid(data_json)));
CREATE TABLE engine_handoff (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), old_holder TEXT NOT NULL, new_holder TEXT NOT NULL,
    old_epoch INTEGER NOT NULL, next_epoch INTEGER NOT NULL, state TEXT NOT NULL, reason TEXT);
CREATE TABLE engine_message (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    from_session_id TEXT REFERENCES engine_session(id), sender_kind TEXT NOT NULL,
    to_session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    step_id TEXT REFERENCES engine_step(id), text TEXT NOT NULL, mode TEXT NOT NULL, state TEXT NOT NULL,
    provider_turn_id TEXT, reason TEXT);
CREATE TABLE engine_meta (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1), schema_version INTEGER NOT NULL,
    machine_id TEXT NOT NULL, next_seq INTEGER NOT NULL, retained_after INTEGER NOT NULL,
    snapshot_seq INTEGER NOT NULL);
CREATE TABLE engine_needs_me (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    step_id TEXT REFERENCES engine_step(id), kind TEXT NOT NULL, text TEXT NOT NULL,
    options_json TEXT NOT NULL CHECK(json_valid(options_json)), provider_request_id TEXT, chat_row_id TEXT,
    state TEXT NOT NULL, deadline_ms INTEGER,
    answer_json TEXT CHECK(answer_json IS NULL OR json_valid(answer_json)), answered_by TEXT, answered_ms INTEGER);
CREATE TABLE engine_outbox (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    event_seq INTEGER NOT NULL, session_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    kind TEXT NOT NULL, payload_json TEXT NOT NULL CHECK(json_valid(payload_json)), state TEXT NOT NULL,
    attempt INTEGER NOT NULL, next_ms INTEGER, receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json)),
    last_error TEXT);
CREATE TABLE engine_pr (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    run_id TEXT NOT NULL REFERENCES engine_run(id), repo TEXT NOT NULL, number INTEGER, head TEXT,
    state TEXT NOT NULL, reported_by TEXT, reported_state TEXT, observed_head TEXT, observed_ms INTEGER, url TEXT);
CREATE TABLE engine_run (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    owner_session_id TEXT NOT NULL REFERENCES engine_session(id), worktree TEXT, branch TEXT,
    pr_id TEXT REFERENCES engine_pr(id), state TEXT NOT NULL, owner_loss_limit_ms INTEGER NOT NULL,
    owner_lost_ms INTEGER);
CREATE TABLE engine_session (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    provider TEXT NOT NULL, model TEXT NOT NULL, folder TEXT NOT NULL, title TEXT NOT NULL,
    state TEXT NOT NULL, activity_state TEXT NOT NULL,
    parent_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    provider_session_id TEXT, holder TEXT NOT NULL, epoch INTEGER NOT NULL,
    reserved_epoch INTEGER, cancel_requested INTEGER NOT NULL, lost_ms INTEGER);
CREATE TABLE engine_step (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    run_id TEXT NOT NULL REFERENCES engine_run(id), name TEXT NOT NULL,
    session_id TEXT REFERENCES engine_session(id), state TEXT NOT NULL, attempt INTEGER NOT NULL,
    max_attempts INTEGER NOT NULL, required INTEGER NOT NULL, rule_skippable INTEGER NOT NULL,
    started_ms INTEGER, ended_ms INTEGER, proof_json TEXT CHECK(proof_json IS NULL OR json_valid(proof_json)),
    outcome_reason TEXT);
CREATE TABLE engine_step_attempt (
    step_id TEXT NOT NULL REFERENCES engine_step(id), attempt INTEGER NOT NULL, state TEXT NOT NULL,
    started_ms INTEGER, ended_ms INTEGER, proof_json TEXT CHECK(proof_json IS NULL OR json_valid(proof_json)),
    reason TEXT, late INTEGER NOT NULL, PRIMARY KEY(step_id,attempt));
CREATE TABLE engine_step_dependency (
    step_id TEXT NOT NULL REFERENCES engine_step(id), dependency_id TEXT NOT NULL REFERENCES engine_step(id),
    optional INTEGER NOT NULL, PRIMARY KEY(step_id,dependency_id));
CREATE TABLE engine_step_fact (
    id TEXT PRIMARY KEY, step_id TEXT NOT NULL REFERENCES engine_step(id), attempt INTEGER NOT NULL,
    kind TEXT NOT NULL, at_ms INTEGER NOT NULL, data_json TEXT NOT NULL CHECK(json_valid(data_json)), late INTEGER NOT NULL);
CREATE TABLE engine_tool_call (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    step_id TEXT REFERENCES engine_step(id), attempt INTEGER, epoch INTEGER NOT NULL, boundary TEXT NOT NULL,
    boundary_id TEXT NOT NULL, tool TEXT NOT NULL, input_preview TEXT, output_preview TEXT,
    input_bytes INTEGER NOT NULL, output_bytes INTEGER, started_ms INTEGER NOT NULL, ended_ms INTEGER,
    outcome TEXT, late INTEGER NOT NULL, UNIQUE(boundary,boundary_id));
CREATE TABLE engine_usage (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    step_id TEXT REFERENCES engine_step(id), attempt INTEGER, epoch INTEGER NOT NULL, provider_turn_id TEXT NOT NULL,
    input_tokens INTEGER, output_tokens INTEGER, cached_tokens INTEGER, cost_nanodollars INTEGER,
    duration_ms INTEGER, late INTEGER NOT NULL, UNIQUE(session_id,provider_turn_id));
INSERT INTO "engine_command_receipt" VALUES('ca47bd1b01dc44329355dacd10a2be6c','97afe1dee42226dc87fb8bf2bbd1958a56d57c866595a7a98ce4587c309a14f5','{"applied":true,"late":false,"error":null,"version":1,"seq":1,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('c481f3d4b00e4b1ca990a21c4d1e99f4','15d2c0f0be196207675f2d02e61de1e0ca4a2f99941153fb0207b0c53458c5ba','{"applied":true,"late":false,"error":null,"version":1,"seq":2,"epoch":0}');
INSERT INTO "engine_event" VALUES('38b5ca0cf805474fb768def456e8a3de:1',1,'38b5ca0cf805474fb768def456e8a3de',1000,1000,NULL,1,'message.changed','message','inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('38b5ca0cf805474fb768def456e8a3de:2',1,'38b5ca0cf805474fb768def456e8a3de',1000,1000,NULL,2,'effect.changed','effect','send/inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_message" VALUES('inflight',1,'38b5ca0cf805474fb768def456e8a3de',1000,1000,NULL,NULL,'window','inflight',NULL,NULL,'actual crash','prompt','queued',NULL,NULL);
INSERT INTO "engine_meta" VALUES(1,1,'38b5ca0cf805474fb768def456e8a3de',3,0,0);
INSERT INTO "engine_outbox" VALUES('send/inflight',1,'38b5ca0cf805474fb768def456e8a3de',1000,1000,NULL,1,'inflight',NULL,'send','{"fromSessionId":null,"toSessionId":"inflight","text":"actual crash","mode":"prompt"}','inflight',1,NULL,NULL,NULL);
INSERT INTO "engine_session" VALUES('inflight',0,'38b5ca0cf805474fb768def456e8a3de',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
CREATE INDEX engine_message_recipient_state ON engine_message(to_session_id,state);
CREATE UNIQUE INDEX engine_needs_me_provider_request ON engine_needs_me(session_id,provider_request_id)
    WHERE provider_request_id IS NOT NULL;
CREATE INDEX engine_needs_me_state_deadline ON engine_needs_me(state,deadline_ms);
CREATE INDEX engine_outbox_session_order ON engine_outbox(session_id,event_seq);
CREATE INDEX engine_outbox_state_next ON engine_outbox(state,next_ms);
CREATE INDEX engine_session_parent_state ON engine_session(parent_id,state);
CREATE INDEX engine_session_run_state ON engine_session(run_id,state);
CREATE INDEX engine_step_run_state ON engine_step(run_id,state);
CREATE INDEX engine_tool_call_run_time ON engine_tool_call(run_id,started_ms);
CREATE INDEX engine_tool_call_session_time ON engine_tool_call(session_id,started_ms);
CREATE INDEX engine_tool_call_step_attempt_time ON engine_tool_call(step_id,attempt,started_ms);
CREATE INDEX engine_usage_run_time ON engine_usage(run_id,created_ms);
CREATE INDEX engine_usage_session_time ON engine_usage(session_id,created_ms);
CREATE INDEX engine_usage_step_attempt_time ON engine_usage(step_id,attempt,created_ms);
