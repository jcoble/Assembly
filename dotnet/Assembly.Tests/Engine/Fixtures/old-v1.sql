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
INSERT INTO "engine_command_receipt" VALUES('00000001000000000000000000000000','110d5f91ad930f6e3a765325c3e5f4e7a63723d77ca1b7910edae59c9ba80830','{"applied":true,"late":false,"error":null,"version":1,"seq":1,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000002000000000000000000000000','c1603e7a1252b63750a17bf60a424c8c50ba54c5e98c2b854451c2f21f1ae772','{"applied":true,"late":false,"error":null,"version":1,"seq":2,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000003000000000000000000000000','28d3d3974a2efc5f2898c527f70a9845bd530d9666bfe750965f595845704559','{"applied":true,"late":false,"error":null,"version":1,"seq":3,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000004000000000000000000000000','2086433810a298a5cb1a8943c50aea0e8804f36f45cfd4830de6cec8cb61a15e','{"applied":true,"late":false,"error":null,"version":1,"seq":4,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000005000000000000000000000000','ea198718b0f0afb2d6bcde689947f88fcf8c2beb8c16246fd2ad05811183b44c','{"applied":true,"late":false,"error":null,"version":1,"seq":5,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000006000000000000000000000000','a51cdd82c0280f96a31cb01b89c9a3242a66a731a82f8060a99945c3d128ad69','{"applied":true,"late":false,"error":null,"version":1,"seq":6,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000007000000000000000000000000','2acc2751d32603edee299231f22fc44083d11c46dc5ad3b2b766a44cb5a91689','{"applied":true,"late":false,"error":null,"version":1,"seq":7,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000008000000000000000000000000','2f997b5dedad2df90189828c9ed1ccac047796f624fefd4d9d49c5448f145c4c','{"applied":true,"late":false,"error":null,"version":1,"seq":8,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000009000000000000000000000000','8742dc5719a4f014d32c2660a7c6e661750d62920eea990cbddfa6be66777ec7','{"applied":true,"late":false,"error":null,"version":1,"seq":9,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000a000000000000000000000000','e59ea6b6b4e662aa915c4f5a0547b8ee24897ed8861d57783d8f594d923ab104','{"applied":true,"late":false,"error":null,"version":1,"seq":10,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000b000000000000000000000000','7babda50811ef271d343e4b14688267f159c0f97d712f2086c44e5b201bea4dd','{"applied":true,"late":false,"error":null,"version":1,"seq":11,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000c000000000000000000000000','4a83377494d438e41f91eeba58a70e1b8816a0e7a9a2462911b73aa4f2617e2f','{"applied":true,"late":false,"error":null,"version":1,"seq":12,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000d000000000000000000000000','7f1d4cc50d7248031c23f44c66e05b50932247c64df2ef2590e814565ca69179','{"applied":true,"late":false,"error":null,"version":1,"seq":13,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000e000000000000000000000000','e0020365f24c13e85b7a2f50bdb601c5d5d19835d9dd43d0f6e6ac37d0ffccc7','{"applied":true,"late":false,"error":null,"version":1,"seq":14,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000f000000000000000000000000','ced4e92721da7bd9d5a68dbf4b86a8920f981c840f3dd5deabca90360aaf0797','{"applied":true,"late":false,"error":null,"version":1,"seq":15,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000010000000000000000000000000','76d513041804296731551685eedd2f6731070962fbdc19b958817dde6ef028e6','{"applied":true,"late":false,"error":null,"version":1,"seq":16,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000011000000000000000000000000','bd7ad096740d55282161fa1cb2118f68f495684974841fd735df92554f396e1c','{"applied":true,"late":false,"error":null,"version":1,"seq":17,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000012000000000000000000000000','4d2a697711968ce5b50a44da38c84ba4695701e0ae7941796d3e86c1252a5f53','{"applied":true,"late":false,"error":null,"version":1,"seq":18,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000013000000000000000000000000','e6218d81209b0a9124d7c13cb6e2bff8dfc8c1d5f2d77bb1180d96e7553f761e','{"applied":true,"late":false,"error":null,"version":1,"seq":19,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000014000000000000000000000000','b5df3be1a7e30f7d2848cddd5e7c19dcdfe174505aeb0ad21ee29d9301c130e5','{"applied":true,"late":false,"error":null,"version":1,"seq":20,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000015000000000000000000000000','ec962fd41d82ef445e9b32d07ec4da5709cdd9d90ed155c1228c86297ab9576a','{"applied":true,"late":false,"error":null,"version":1,"seq":21,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000016000000000000000000000000','baa8b5cd1e9a82585d99e8bf90b0fb28cf820f1802b77d7eab796e54f6e31f84','{"applied":true,"late":false,"error":null,"version":1,"seq":22,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000017000000000000000000000000','8606cdf4dbf00db51992d91995420789618a12810dffe630245abd81f3225732','{"applied":true,"late":false,"error":null,"version":1,"seq":23,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000018000000000000000000000000','115dfc63c9e96aa4575bb5d859c75496a5296341b4fe713a115905d70c14253c','{"applied":true,"late":false,"error":null,"version":2,"seq":24,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000019000000000000000000000000','3da64c2ae926e39cc060ae9d36249d38794777e6ac4d8bce9563089dee71e273','{"applied":true,"late":false,"error":null,"version":2,"seq":25,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001a000000000000000000000000','5b11748b3708683a2043719bd697395f4d7df61391f593c2151f8bd0a2eaa886','{"applied":true,"late":false,"error":null,"version":2,"seq":26,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001b000000000000000000000000','2d3ac608503763a846df27a5e13db5687b3bb2374ef554e7ae1b287580af0de0','{"applied":true,"late":false,"error":null,"version":2,"seq":27,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('11111111111111111111111111111111','193b46d244e63f0287ca1aa7f9e0ed7c67f763d2072a75e59eace020215488e8','{"applied":true,"late":false,"error":null,"version":3,"seq":28,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001c000000000000000000000000','93af6d3a4cc25d640b2d7a00e4da798bf67b65fe6b4b6c2b73787c29dc9ad126','{"applied":true,"late":false,"error":null,"version":4,"seq":29,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001d000000000000000000000000','3122c670ddb32339fe5e90ec985d55a5da531236ebb7d9aa42984bbd350b328f','{"applied":true,"late":false,"error":null,"version":2,"seq":30,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001e000000000000000000000000','752602fdb74665d673cdd3f95f1764f3d1afbe18e4c32c7dcb9f41d87b718e20','{"applied":true,"late":false,"error":null,"version":2,"seq":32,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001f000000000000000000000000','dda55578d7b8892563671e45d5c66bb67287c7f8569281bcd8cf7ba38323dfad','{"applied":true,"late":false,"error":null,"version":2,"seq":34,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000020000000000000000000000000','50b704f73ff3ec2bb055380eadf1b824693f19d00088897a9c672721fc08c161','{"applied":true,"late":false,"error":null,"version":2,"seq":36,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000021000000000000000000000000','46d817dd63233b320e2e47080bd2362fc83ecc61341b56d107297fc1358ef096','{"applied":true,"late":false,"error":null,"version":2,"seq":38,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000022000000000000000000000000','275c4cec7056c1263d71020840ba44bbd5d9f8756c84d02ce2a2a9f5321a701c','{"applied":true,"late":false,"error":null,"version":2,"seq":39,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000023000000000000000000000000','f800c5fde07e8d032f889716bbe309d3d07b7ecffebdb8d54236f75f95c1bba5','{"applied":true,"late":false,"error":null,"version":1,"seq":41,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000024000000000000000000000000','edcb5b8b26e41b52056d42f5e4506e7ed1f4cc1c2a9f55e8a8097b8db62dcf30','{"applied":true,"late":false,"error":null,"version":1,"seq":43,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000025000000000000000000000000','0a93f6a6c11c66aa70e079f2d86cbd5ee9af9f3daa5223718e96a19ba120c638','{"applied":true,"late":false,"error":null,"version":1,"seq":45,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000026000000000000000000000000','1f369964d6be7bb94e2fecf52beb7c92a03f14765e4aa4071a82cd72ec02fc86','{"applied":true,"late":false,"error":null,"version":2,"seq":47,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000027000000000000000000000000','42d207d7e9ad4f9d9ea2806c3a570ac62317eeda096663603fa9a0a1fe85743b','{"applied":true,"late":true,"error":null,"version":2,"seq":48,"epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:1',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,1,'message.changed','message','pending',1,1000,0,'{"sessionId":"pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:2',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,2,'message.changed','message','pending-2',1,1000,0,'{"sessionId":"pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:3',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,3,'message.changed','message','transient',1,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:4',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,4,'message.changed','message','inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:5',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,5,'message.changed','message','uncertain',1,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:6',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,6,'message.changed','message','unavailable',1,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:7',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,7,'message.changed','message','unknown-unavailable',1,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:8',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,8,'message.changed','message','failed',1,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:9',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,9,'message.changed','message','terminal-pending',1,1000,0,'{"sessionId":"terminal-pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:10',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,10,'message.changed','message','terminal-delivered_to_turn',1,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:11',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,11,'message.changed','message','terminal-next_turn',1,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:12',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,12,'message.changed','message','terminal-unsupported',1,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:13',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,13,'message.changed','message','terminal-failed',1,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:14',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,14,'effect.changed','effect','send/transient',1,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:15',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,15,'effect.changed','effect','send/inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:16',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,16,'effect.changed','effect','send/uncertain',1,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:17',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,17,'effect.changed','effect','send/unavailable',1,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:18',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,18,'effect.changed','effect','send/unknown-unavailable',1,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:19',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,19,'effect.changed','effect','send/failed',1,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:20',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,20,'effect.changed','effect','send/terminal-delivered_to_turn',1,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:21',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,21,'effect.changed','effect','send/terminal-next_turn',1,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:22',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,22,'effect.changed','effect','send/terminal-unsupported',1,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:23',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,23,'effect.changed','effect','send/terminal-failed',1,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:24',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,24,'effect.changed','effect','send/transient',2,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"pending","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:25',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,25,'effect.changed','effect','send/uncertain',2,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"uncertain","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:26',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,26,'effect.changed','effect','send/unavailable',2,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"unavailable","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:27',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,27,'effect.changed','effect','send/unknown-unavailable',2,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"uncertain","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:28',3,'7876fc7e252f4fc7b7c1dfcccb767b53',2000,2000,NULL,28,'effect.changed','effect','send/unknown-unavailable',3,2000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:29',4,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,29,'effect.changed','effect','send/unknown-unavailable',4,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"unavailable","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:30',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,30,'effect.changed','effect','send/failed',2,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:31',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,31,'message.changed','message','terminal-delivered_to_turn',2,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"delivered_to_turn","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:32',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,32,'effect.changed','effect','send/terminal-delivered_to_turn',2,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:33',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,33,'message.changed','message','terminal-next_turn',2,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"next_turn","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:34',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,34,'effect.changed','effect','send/terminal-next_turn',2,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:35',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,35,'message.changed','message','terminal-unsupported',2,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"unsupported","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:36',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,36,'effect.changed','effect','send/terminal-unsupported',2,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:37',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,37,'message.changed','message','terminal-failed',2,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:38',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,38,'effect.changed','effect','send/terminal-failed',2,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:39',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,39,'message.changed','message','terminal-pending',2,1000,0,'{"sessionId":"terminal-pending","runId":null,"stepId":null,"parentId":null,"state":"unsupported","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:40',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,40,'message.changed','message','held',1,1000,0,'{"sessionId":"held","runId":null,"stepId":null,"parentId":null,"state":"held","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:41',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,41,'session.cancel_requested','session','held',1,1000,0,'{"sessionId":"held","runId":null,"stepId":null,"parentId":null,"state":"idle","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:42',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,42,'session.changed','session','child/x',1,1000,0,'{"sessionId":"child/x","runId":null,"stepId":null,"parentId":"child-parent","state":"ended","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:43',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,43,'message.changed','message','child-ended/child/x',1,1000,0,'{"sessionId":"child-parent","runId":null,"stepId":null,"parentId":"child/x","state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:44',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,44,'step.changed','step','history-step',1,1000,0,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"running","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:45',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,45,'run.changed','run','history-run',1,1000,0,'{"sessionId":"pending","runId":"history-run","stepId":null,"parentId":null,"state":"running","epoch":null}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:46',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,46,'step.changed','step','history-step',2,1000,0,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:47',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,47,'run.changed','run','history-run',2,1000,0,'{"sessionId":"pending","runId":"history-run","stepId":null,"parentId":null,"state":"failed","epoch":null}');
INSERT INTO "engine_event" VALUES('7876fc7e252f4fc7b7c1dfcccb767b53:48',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,48,'step.late','step','history-step',2,1000,1,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"failed","epoch":0}');
INSERT INTO "engine_message" VALUES('pending',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','pending',NULL,NULL,'body pending','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('pending-2',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','pending',NULL,NULL,'second','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('transient',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','transient',NULL,NULL,'body transient','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('inflight',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','inflight',NULL,NULL,'body inflight','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('uncertain',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','uncertain',NULL,NULL,'body uncertain','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('unavailable',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','unavailable',NULL,NULL,'body unavailable','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('unknown-unavailable',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','unknown-unavailable',NULL,NULL,'body unknown-unavailable','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('failed',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','failed',NULL,NULL,'body failed','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('terminal-pending',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','terminal-pending',NULL,NULL,'body terminal-pending','prompt','unsupported','old-turn','historical');
INSERT INTO "engine_message" VALUES('terminal-delivered_to_turn',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','terminal-delivered_to_turn',NULL,NULL,'body terminal-delivered_to_turn','prompt','delivered_to_turn','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-next_turn',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','terminal-next_turn',NULL,NULL,'body terminal-next_turn','prompt','next_turn','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-unsupported',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','terminal-unsupported',NULL,NULL,'body terminal-unsupported','prompt','unsupported','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-failed',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','terminal-failed',NULL,NULL,'body terminal-failed','prompt','failed','old-turn',NULL);
INSERT INTO "engine_message" VALUES('held',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,NULL,'window','held',NULL,NULL,'interrupt','interrupt','held',NULL,NULL);
INSERT INTO "engine_message" VALUES('child-ended/child/x',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'child/x','system','child-parent',NULL,NULL,'Child session child/x ended.','prompt','queued',NULL,NULL);
INSERT INTO "engine_meta" VALUES(1,1,'7876fc7e252f4fc7b7c1dfcccb767b53',49,1,2);
INSERT INTO "engine_outbox" VALUES('send/pending',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,1,'pending',NULL,'send','{"fromSessionId":null,"toSessionId":"pending","text":"body pending","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/pending-2',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,2,'pending',NULL,'send','{"fromSessionId":null,"toSessionId":"pending","text":"second","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/transient',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,3,'transient',NULL,'send','{"fromSessionId":null,"toSessionId":"transient","text":"body transient","mode":"prompt"}','pending',1,2000,'{"disposition":1,"data":null,"error":"retry"}','retry');
INSERT INTO "engine_outbox" VALUES('send/inflight',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,4,'inflight',NULL,'send','{"fromSessionId":null,"toSessionId":"inflight","text":"body inflight","mode":"prompt"}','inflight',1,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/uncertain',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,5,'uncertain',NULL,'send','{"fromSessionId":null,"toSessionId":"uncertain","text":"body uncertain","mode":"prompt"}','uncertain',1,NULL,'{"disposition":4,"data":null,"error":"interrupted"}','interrupted');
INSERT INTO "engine_outbox" VALUES('send/unavailable',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,6,'unavailable',NULL,'send','{"fromSessionId":null,"toSessionId":"unavailable","text":"body unavailable","mode":"prompt"}','unavailable',1,NULL,'{"disposition":2,"data":null,"error":"offline"}','offline');
INSERT INTO "engine_outbox" VALUES('send/unknown-unavailable',4,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,7,'unknown-unavailable',NULL,'send','{"fromSessionId":null,"toSessionId":"unknown-unavailable","text":"body unknown-unavailable","mode":"prompt"}','unavailable',2,NULL,'{"disposition":4,"data":null,"error":"offline"}','offline');
INSERT INTO "engine_outbox" VALUES('send/failed',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,8,'failed',NULL,'send','{"fromSessionId":null,"toSessionId":"failed","text":"body failed","mode":"prompt"}','failed',1,NULL,'{"disposition":3,"data":null,"error":"permanent"}','permanent');
INSERT INTO "engine_outbox" VALUES('send/terminal-pending',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,9,'terminal-pending',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-pending","text":"body terminal-pending","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-delivered_to_turn',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,10,'terminal-delivered_to_turn',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-delivered_to_turn","text":"body terminal-delivered_to_turn","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"delivered_to_turn","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-next_turn',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,11,'terminal-next_turn',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-next_turn","text":"body terminal-next_turn","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"next_turn","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-unsupported',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,12,'terminal-unsupported',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-unsupported","text":"body terminal-unsupported","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"unsupported","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-failed',2,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,13,'terminal-failed',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-failed","text":"body terminal-failed","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"failed","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('message-stop/held',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,41,'held',NULL,'stop','{}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/child-ended/child/x',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,43,'child-parent',NULL,'send','{"fromSessionId":"child/x","toSessionId":"child-parent","text":"Child session child/x ended.","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_run" VALUES('history-run',2,'7876fc7e252f4fc7b7c1dfcccb767b53',2000,1000,NULL,'pending',NULL,NULL,NULL,'failed',60000,NULL);
INSERT INTO "engine_session" VALUES('pending',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,'history-run',NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('transient',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('inflight',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('uncertain',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('unavailable',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('unknown-unavailable',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('failed',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('held',1,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,1,NULL);
INSERT INTO "engine_session" VALUES('terminal-pending',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-delivered_to_turn',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-next_turn',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-unsupported',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-failed',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('child-parent',0,'7876fc7e252f4fc7b7c1dfcccb767b53',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('child/x',1,'7876fc7e252f4fc7b7c1dfcccb767b53',2000,1000,NULL,'codex','model','/test','Test','ended','idle','child-parent',NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('history-worker',0,'7876fc7e252f4fc7b7c1dfcccb767b53',2000,2000,NULL,'codex','model','/test','Test','idle','idle','pending','history-run',NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_step" VALUES('history-step',2,'7876fc7e252f4fc7b7c1dfcccb767b53',2000,1000,NULL,'history-run','history-step','history-worker','failed',1,1,1,0,1000,1000,'[]','old failure');
INSERT INTO "engine_step_attempt" VALUES('history-step',1,'failed',1000,1000,'[]','late failure',1);
INSERT INTO "engine_step_fact" VALUES('00000027000000000000000000000000','history-step',1,'failed',1000,'{"state":"failed","attempt":1,"proofUrls":[],"reason":"late failure","byWorkflowRule":false}',1);
INSERT INTO "rows" VALUES('old-chat',1,'turn','old-chat','message','{"blocks":[]}',1000);
INSERT INTO "sessions" VALUES('old-chat',2);
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
