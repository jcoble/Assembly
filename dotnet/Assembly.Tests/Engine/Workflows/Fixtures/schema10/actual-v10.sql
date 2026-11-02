-- Actual unchanged producer75e5c7cf4bed90bdf5e9eb3e4bf5674b7a15a704, captured before workflow migration edits.
PRAGMA defer_foreign_keys=ON;
CREATE TABLE engine_command_receipt (
    command_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, result_json TEXT NOT NULL CHECK(json_valid(result_json)));
CREATE TABLE engine_event (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
seq INTEGER NOT NULL, kind TEXT NOT NULL, entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
entity_version INTEGER NOT NULL, at_ms INTEGER NOT NULL, late INTEGER NOT NULL,
data_json TEXT NOT NULL CHECK(json_valid(data_json)), UNIQUE(machine,seq));
CREATE TABLE engine_handoff (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), old_holder TEXT NOT NULL, new_holder TEXT NOT NULL,
    old_epoch INTEGER NOT NULL, next_epoch INTEGER NOT NULL, state TEXT NOT NULL, reason TEXT);
CREATE TABLE engine_message (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
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
CREATE TABLE engine_meta (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1), schema_version INTEGER NOT NULL,
    machine_id TEXT NOT NULL, next_seq INTEGER NOT NULL, retained_after INTEGER NOT NULL,
    snapshot_seq INTEGER NOT NULL);
CREATE TABLE engine_mirror (
    machine TEXT PRIMARY KEY, name TEXT NOT NULL, generation INTEGER NOT NULL, through INTEGER NOT NULL,
    at_json TEXT CHECK(at_json IS NULL OR json_valid(at_json)), retained_after INTEGER NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('snapshotting','live','retired')), updated_ms INTEGER NOT NULL);
CREATE TABLE engine_mirror_row (
    machine TEXT NOT NULL REFERENCES engine_mirror(machine), entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
    generation INTEGER NOT NULL, version INTEGER NOT NULL, row_json TEXT NOT NULL CHECK(json_valid(row_json)),
    PRIMARY KEY(machine,generation,entity_kind,entity_id));
CREATE TABLE engine_needs_me (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    step_id TEXT REFERENCES engine_step(id), kind TEXT NOT NULL, text TEXT NOT NULL,
    options_json TEXT NOT NULL CHECK(json_valid(options_json)), provider_request_id TEXT, chat_row_id TEXT,
    state TEXT NOT NULL, deadline_ms INTEGER,
    addressee_session_id TEXT NULL REFERENCES engine_session(id),
    answer_json TEXT CHECK(answer_json IS NULL OR json_valid(answer_json)), answered_by TEXT, answered_ms INTEGER);
CREATE TABLE engine_outbox (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    event_seq INTEGER NOT NULL, session_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    kind TEXT NOT NULL, payload_json TEXT NOT NULL CHECK(json_valid(payload_json)), state TEXT NOT NULL,
    attempt INTEGER NOT NULL, next_ms INTEGER, receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json)),
    last_error TEXT, order_key TEXT NOT NULL DEFAULT '');
CREATE TABLE engine_pr (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    run_id TEXT NOT NULL REFERENCES engine_run(id), repo TEXT NOT NULL, number INTEGER, head TEXT,
    state TEXT NOT NULL, reported_by TEXT, reported_state TEXT, observed_head TEXT, observed_ms INTEGER, url TEXT);
CREATE TABLE engine_provider (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    provider TEXT NOT NULL, account_context TEXT NOT NULL,
    installation TEXT NOT NULL, auth_state TEXT NOT NULL, reason TEXT NULL,
    facts_json TEXT NULL CHECK(facts_json IS NULL OR json_valid(facts_json)), checked_ms INTEGER NULL);
CREATE TABLE engine_run (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    owner_session_id TEXT NOT NULL REFERENCES engine_session(id), worktree TEXT, branch TEXT,
    pr_id TEXT REFERENCES engine_pr(id), state TEXT NOT NULL, owner_loss_limit_ms INTEGER NOT NULL,
    owner_lost_ms INTEGER);
CREATE TABLE engine_session (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    provider TEXT NOT NULL, model TEXT NULL, folder TEXT NOT NULL, title TEXT NOT NULL,
    account_context TEXT NOT NULL DEFAULT '',
    project_id TEXT NULL, branch TEXT NULL,
    checkout_state TEXT NOT NULL DEFAULT 'ready' CHECK(checkout_state IN ('ready','pending','refused')),
    checkout_operation_id TEXT NULL,
    checkout_intent_json TEXT NULL CHECK(checkout_intent_json IS NULL OR json_valid(checkout_intent_json)),
    managed_checkout INTEGER NOT NULL DEFAULT 0 CHECK(managed_checkout IN (0,1)),
    state TEXT NOT NULL, activity_state TEXT NOT NULL,
    parent_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
    provider_session_id TEXT, holder TEXT NOT NULL, epoch INTEGER NOT NULL,
    reserved_epoch INTEGER, cancel_requested INTEGER NOT NULL, lost_ms INTEGER, can_steer INTEGER NOT NULL DEFAULT 0);
CREATE TABLE engine_sign_in (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
    provider TEXT NOT NULL, account_context TEXT NOT NULL, method_id TEXT NULL,
    state TEXT NOT NULL, reason TEXT NULL, deadline_ms INTEGER NOT NULL,
    origin_need_id TEXT NULL REFERENCES engine_needs_me(id));
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
CREATE INDEX engine_event_machine_seq ON engine_event(machine,seq);
CREATE INDEX engine_message_pending_expiry ON engine_message(machine,expires_ms)
WHERE state IN ('queued','held') AND expires_ms IS NOT NULL;
CREATE INDEX engine_message_recipient_state ON engine_message(to_session_id,state);
CREATE UNIQUE INDEX engine_needs_me_provider_request ON engine_needs_me(session_id,provider_request_id)
    WHERE provider_request_id IS NOT NULL;
CREATE INDEX engine_needs_me_state_deadline ON engine_needs_me(state,deadline_ms);
CREATE INDEX engine_outbox_order ON engine_outbox(order_key,event_seq);
CREATE INDEX engine_outbox_session_order ON engine_outbox(session_id,event_seq);
CREATE INDEX engine_outbox_state_next ON engine_outbox(state,next_ms);
CREATE INDEX engine_session_parent_state ON engine_session(parent_id,state);
CREATE INDEX engine_session_run_state ON engine_session(run_id,state);
CREATE INDEX engine_sign_in_active_deadline ON engine_sign_in(deadline_ms,id)
    WHERE state IN ('starting','waiting_for_person','finishing');
CREATE UNIQUE INDEX engine_sign_in_active_target ON engine_sign_in(machine,provider,account_context)
    WHERE state IN ('starting','waiting_for_person','finishing');
CREATE INDEX engine_step_run_state ON engine_step(run_id,state);
CREATE INDEX engine_tool_call_run_time ON engine_tool_call(run_id,started_ms);
CREATE INDEX engine_tool_call_session_time ON engine_tool_call(session_id,started_ms);
CREATE INDEX engine_tool_call_step_attempt_time ON engine_tool_call(step_id,attempt,started_ms);
CREATE INDEX engine_usage_run_time ON engine_usage(run_id,created_ms);
CREATE INDEX engine_usage_session_time ON engine_usage(session_id,created_ms);
CREATE INDEX engine_usage_step_attempt_time ON engine_usage(step_id,attempt,created_ms);
INSERT INTO "engine_command_receipt"("command_id","fingerprint","result_json") VALUES(CAST(X'3531623935316338343961353436656261663862373139383933623333323465' AS TEXT),CAST(X'33306465643832363961313963316531306435626436306361336430656230333538663334346466336166393935356666643034353738303339303935643665' AS TEXT),CAST(X'7b226170706c696564223a747275652c226c617465223a66616c73652c226572726f72223a6e756c6c2c2276657273696f6e223a312c22736571223a312c2265706f6368223a307d' AS TEXT));
INSERT INTO "engine_command_receipt"("command_id","fingerprint","result_json") VALUES(CAST(X'3063653338363030616233663437303439383762653866363738623065653830' AS TEXT),CAST(X'64323234643138636232316566666364653635323266316339383336313962366333376239646162333861663038323862366164656561643034653839633536' AS TEXT),CAST(X'7b226170706c696564223a747275652c226c617465223a66616c73652c226572726f72223a6e756c6c2c2276657273696f6e223a322c22736571223a322c2265706f6368223a307d' AS TEXT));
INSERT INTO "engine_event"("id","version","machine","created_ms","updated_ms","task_url","seq","kind","entity_kind","entity_id","entity_version","at_ms","late","data_json") VALUES(CAST(X'37623830366339303230393634393434393761653832616466386236316531633a31' AS TEXT),1,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,1,CAST(X'72756e2e6368616e676564' AS TEXT),CAST(X'72756e' AS TEXT),CAST(X'6f6c642d72756e' AS TEXT),1,1000,0,CAST(X'7b2273657373696f6e4964223a226f776e6572222c2272756e4964223a226f6c642d72756e222c22737465704964223a6e756c6c2c22706172656e744964223a6e756c6c2c227374617465223a22717565756564222c2265706f6368223a6e756c6c7d' AS TEXT));
INSERT INTO "engine_event"("id","version","machine","created_ms","updated_ms","task_url","seq","kind","entity_kind","entity_id","entity_version","at_ms","late","data_json") VALUES(CAST(X'37623830366339303230393634393434393761653832616466386236316531633a32' AS TEXT),2,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,2,CAST(X'6d6573736167652e6368616e676564' AS TEXT),CAST(X'6d657373616765' AS TEXT),CAST(X'6f6c642d6d657373616765' AS TEXT),2,1000,0,CAST(X'7b2273657373696f6e4964223a226f776e6572222c2272756e4964223a6e756c6c2c22737465704964223a6e756c6c2c22706172656e744964223a6e756c6c2c227374617465223a22717565756564222c2265706f6368223a307d' AS TEXT));
INSERT INTO "engine_message"("id","version","machine","created_ms","updated_ms","task_url","from_session_id","sender_kind","to_session_id","run_id","step_id","text","mode","state","provider_turn_id","reason","delivery","attempt","in_flight","sender_label","client_id","expires_ms","framed","attachment_ids_json","held_epoch","target_epoch","owner_content_sha256") VALUES(CAST(X'6f6c642d6d657373616765' AS TEXT),2,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,NULL,CAST(X'77696e646f77' AS TEXT),CAST(X'6f776e6572' AS TEXT),NULL,NULL,CAST(X'70726573657276656420717565756564206d657373616765' AS TEXT),CAST(X'70726f6d7074' AS TEXT),CAST(X'717565756564' AS TEXT),NULL,NULL,CAST(X'7374656572' AS TEXT),1,1,CAST(X'4a65737365' AS TEXT),NULL,1801000,1,CAST(X'5b5d' AS TEXT),NULL,NULL,NULL);
INSERT INTO "engine_meta"("singleton","schema_version","machine_id","next_seq","retained_after","snapshot_seq") VALUES(1,10,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),3,1,2);
INSERT INTO "engine_needs_me"("id","version","machine","created_ms","updated_ms","task_url","session_id","run_id","step_id","kind","text","options_json","provider_request_id","chat_row_id","state","deadline_ms","addressee_session_id","answer_json","answered_by","answered_ms") VALUES(CAST(X'6f6c642d6e656564' AS TEXT),1,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,CAST(X'6f776e6572' AS TEXT),NULL,NULL,CAST(X'617070726f76616c' AS TEXT),CAST(X'507265736572766564207175657374696f6e' AS TEXT),CAST(X'5b5d' AS TEXT),NULL,NULL,CAST(X'6f70656e' AS TEXT),NULL,NULL,NULL,NULL,NULL);
INSERT INTO "engine_outbox"("id","version","machine","created_ms","updated_ms","task_url","event_seq","session_id","run_id","kind","payload_json","state","attempt","next_ms","receipt_json","last_error","order_key") VALUES(CAST(X'73656e642f6f6c642d6d6573736167652f31' AS TEXT),0,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,2,CAST(X'6f776e6572' AS TEXT),NULL,CAST(X'73656e64' AS TEXT),CAST(X'7b226d6573736167654964223a226f6c642d6d657373616765222c22746f53657373696f6e4964223a226f776e6572222c2274657874223a225b6d6573736167652066726f6d204a657373655d5c6e70726573657276656420717565756564206d657373616765222c2273656e6465724c6162656c223a224a65737365222c22686f77223a2270726f6d7074222c22636c69656e744964223a226f6c642d6d657373616765222c22726563697069656e7456657273696f6e223a307d' AS TEXT),CAST(X'70656e64696e67' AS TEXT),0,NULL,NULL,NULL,CAST(X'6f776e6572' AS TEXT));
INSERT INTO "engine_run"("id","version","machine","created_ms","updated_ms","task_url","owner_session_id","worktree","branch","pr_id","state","owner_loss_limit_ms","owner_lost_ms") VALUES(CAST(X'6f6c642d72756e' AS TEXT),1,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,CAST(X'68747470733a2f2f6578616d706c652e636f6d2f7461736b' AS TEXT),CAST(X'6f776e6572' AS TEXT),NULL,NULL,NULL,CAST(X'717565756564' AS TEXT),30000,NULL);
INSERT INTO "engine_session"("id","version","machine","created_ms","updated_ms","task_url","provider","model","folder","title","account_context","project_id","branch","checkout_state","checkout_operation_id","checkout_intent_json","managed_checkout","state","activity_state","parent_id","run_id","provider_session_id","holder","epoch","reserved_epoch","cancel_requested","lost_ms","can_steer") VALUES(CAST(X'6f776e6572' AS TEXT),0,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,NULL,CAST(X'636f646578' AS TEXT),CAST(X'6d6f64656c' AS TEXT),CAST(X'2f74657374' AS TEXT),CAST(X'54657374' AS TEXT),CAST(X'' AS TEXT),NULL,NULL,CAST(X'7265616479' AS TEXT),NULL,NULL,0,CAST(X'69646c65' AS TEXT),CAST(X'69646c65' AS TEXT),NULL,NULL,NULL,CAST(X'6c6f63616c' AS TEXT),0,2,0,NULL,0);
INSERT INTO "engine_step"("id","version","machine","created_ms","updated_ms","task_url","run_id","name","session_id","state","attempt","max_attempts","required","rule_skippable","started_ms","ended_ms","proof_json","outcome_reason") VALUES(CAST(X'6f6c642d73746570' AS TEXT),1,CAST(X'3762383036633930323039363439343439376165383261646638623631653163' AS TEXT),1000,1000,CAST(X'68747470733a2f2f6578616d706c652e636f6d2f7461736b' AS TEXT),CAST(X'6f6c642d72756e' AS TEXT),CAST(X'507265736572766564' AS TEXT),NULL,CAST(X'70656e64696e67' AS TEXT),0,2,1,0,NULL,NULL,NULL,NULL);
