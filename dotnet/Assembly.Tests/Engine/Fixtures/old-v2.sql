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
    provider_turn_id TEXT, reason TEXT);
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
INSERT INTO "engine_command_receipt" VALUES('00000001000000000000000000000000','eb8eae8af53f2bf305652952a75fb4fd269d01e2cbb8bd9e9299027bd5c0b7bf','{"applied":true,"late":false,"error":null,"version":1,"seq":1,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000002000000000000000000000000','0795db2e77473b819b1b883acd4fd8da6ae2b4f14c11ccddfe5f9f9f5e1b5584','{"applied":true,"late":false,"error":null,"version":1,"seq":2,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000003000000000000000000000000','f3a10529c5169656f29472c07831edfba6c1c5d72019de97281aca5d89fd5f30','{"applied":true,"late":false,"error":null,"version":1,"seq":3,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000004000000000000000000000000','b23175a83c9a4a8b4a746fc2e07ea8e328c50528998a4b900a52a931845a004e','{"applied":true,"late":false,"error":null,"version":1,"seq":4,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000005000000000000000000000000','ea3f978b20c4af620660c12e108ddad289ad23e1c16ed919418665ced79dbc23','{"applied":true,"late":false,"error":null,"version":1,"seq":5,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000006000000000000000000000000','bf60196802161431ba3033b2217664e29a5d6f3b7bfa07be7095b2018b9c06dc','{"applied":true,"late":false,"error":null,"version":1,"seq":6,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000007000000000000000000000000','57b29db9176800ba91a1dfc1f38c5e83a535c54b3d92421afa6d631c69b8b63f','{"applied":true,"late":false,"error":null,"version":1,"seq":7,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000008000000000000000000000000','8187583f43485094ea37bd068ad2574441690b6ecedbddb938bdb7a94ee486b6','{"applied":true,"late":false,"error":null,"version":1,"seq":8,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000009000000000000000000000000','cb78a19106b013d4594098508da33fbfbed9cb096c23e576823a6957dc9c94e4','{"applied":true,"late":false,"error":null,"version":1,"seq":9,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000a000000000000000000000000','3f487548da006bca48b89ac3a03a4d5fecfc5a045fb74cd860fea5218e3c9d8f','{"applied":true,"late":false,"error":null,"version":1,"seq":10,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000b000000000000000000000000','2a325eb48c170eb9930ec040460237b177fd9581f763936c14185ff064eb7a2d','{"applied":true,"late":false,"error":null,"version":1,"seq":11,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000c000000000000000000000000','8f0110c7e94d219fcbaf025f898ccb186077795d755e2af971abe024e87a9f62','{"applied":true,"late":false,"error":null,"version":1,"seq":12,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000d000000000000000000000000','5633e64aee36e655ac6e4c49afffa96af5bd5352302cd7bfd10b57e2d50e00f2','{"applied":true,"late":false,"error":null,"version":1,"seq":13,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000e000000000000000000000000','1695c24e39f5084d709496510b2ed55f02e3e09a5ce641cb1ea171412e046966','{"applied":true,"late":false,"error":null,"version":1,"seq":14,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000000f000000000000000000000000','ed17fab9091ba4d37094318894feba19c0edeea2b2b7c6ec4527abc13d2a53e9','{"applied":true,"late":false,"error":null,"version":1,"seq":15,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000010000000000000000000000000','f92f862b0703bdbe14d07ed25417de7063bf987b99168c7d36bd27bb7be1c9dd','{"applied":true,"late":false,"error":null,"version":1,"seq":16,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000011000000000000000000000000','75dc563a69bd56d9b9d05c87196159e18c32f0b35d1745ee1585b3f4fdbc0778','{"applied":true,"late":false,"error":null,"version":1,"seq":17,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000012000000000000000000000000','6dd5b696e4c7c078ec8ebd16d8603dc4dae894f279e230f336b50283e767e637','{"applied":true,"late":false,"error":null,"version":1,"seq":18,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000013000000000000000000000000','9c117b72b7cd5fda7175b9336d1f906ed402782d15340729f6010ba311f4116a','{"applied":true,"late":false,"error":null,"version":1,"seq":19,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000014000000000000000000000000','9d327802fdaf87d7c65a75ee66d35bf6d3643aff97fec3d8e034fd600f5d41ab','{"applied":true,"late":false,"error":null,"version":1,"seq":20,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000015000000000000000000000000','7063a9d056c00964250e5c25586cdcd2579e64faafcac1ee5437d1b19fa0c448','{"applied":true,"late":false,"error":null,"version":1,"seq":21,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000016000000000000000000000000','fdd9affe02ea417b47612c125dbaa63b1bfbd44816acc735dbf4c99470d15fc4','{"applied":true,"late":false,"error":null,"version":1,"seq":22,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000017000000000000000000000000','3929d8082dd95facc20981eb94d0a12e4f07fec51976dad921a7735d644ad9c3','{"applied":true,"late":false,"error":null,"version":1,"seq":23,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000018000000000000000000000000','ac041ad7587096deef1e824422796e1de229b2dfd066719f022e87152d5cc8f3','{"applied":true,"late":false,"error":null,"version":2,"seq":24,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000019000000000000000000000000','1108671e8ee6ee81037c2d788d29b398115317ab11aa3aa40a1fa162db06b213','{"applied":true,"late":false,"error":null,"version":2,"seq":25,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001a000000000000000000000000','af284bf4b96791194f85a487d288e393ba9a2ca7ecafd1c09ed7cf624272add5','{"applied":true,"late":false,"error":null,"version":2,"seq":26,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001b000000000000000000000000','c2bc26aff13df8dc86ec32385fa7e119e33f38693d5db89861c4fa0296ca69b1','{"applied":true,"late":false,"error":null,"version":2,"seq":27,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('11111111111111111111111111111111','fd59c9d166613628cfb392b50db011be7aec5c17a051233c239abc1343eaaca3','{"applied":true,"late":false,"error":null,"version":3,"seq":28,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001c000000000000000000000000','2727cca2e7de959dfe91c697438b0c370c9335dc200dba8cf6a290dd2360a62e','{"applied":true,"late":false,"error":null,"version":4,"seq":29,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001d000000000000000000000000','85c8e4ac23fe7e5e480fee725fb17604fcd99549bb402a02a6b9bd8f12ec53f2','{"applied":true,"late":false,"error":null,"version":2,"seq":30,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001e000000000000000000000000','8f5cfcaf0def6a39df671a8fe3e52191ca1e6b8ad99fb8c704d623e11d05a6ff','{"applied":true,"late":false,"error":null,"version":2,"seq":32,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('0000001f000000000000000000000000','a22e740c2d17eb6a296459530843947a6a0686e7ba6f6b263e4bb166bc99c09c','{"applied":true,"late":false,"error":null,"version":2,"seq":34,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000020000000000000000000000000','7995fa6ed978875c3fe87135e9e418dfbdd093e42c539313dbfa7273b839f281','{"applied":true,"late":false,"error":null,"version":2,"seq":36,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000021000000000000000000000000','0dbf41d3f583943d3163e8429ac94169f7bc8179e591cb1a27c14ebe8832dc61','{"applied":true,"late":false,"error":null,"version":2,"seq":38,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000022000000000000000000000000','737265cff9311226eba89c97dfa927d3818424ec7e54ac39984b4dd60d99b85f','{"applied":true,"late":false,"error":null,"version":2,"seq":39,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000023000000000000000000000000','2caccd4789c66b2f48d3ce65503b9452b9b9ea4360cc4a74f716adb982deac77','{"applied":true,"late":false,"error":null,"version":1,"seq":41,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000024000000000000000000000000','1d5405e903c210921bbb20c8eac9dbd6c1037db28bc727122792f59821b83113','{"applied":true,"late":false,"error":null,"version":1,"seq":43,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000025000000000000000000000000','1beb50dc47bcc3fbf6e600e1a095451d2c225f637bbe67d4cc4b8ab7ae92d116','{"applied":true,"late":false,"error":null,"version":1,"seq":45,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000026000000000000000000000000','63391a33392d39817fc2b140478abf15d9fb8be5719afc47dfe7b197e0e1af7f','{"applied":true,"late":false,"error":null,"version":2,"seq":47,"epoch":0}');
INSERT INTO "engine_command_receipt" VALUES('00000027000000000000000000000000','0ac9d43b474b0feb42b8adb5179b0c4f709a04aba809c62d0ae8dcc969fd64e0','{"applied":true,"late":true,"error":null,"version":2,"seq":48,"epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:1',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,1,'message.changed','message','pending',1,1000,0,'{"sessionId":"pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:2',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,2,'message.changed','message','pending-2',1,1000,0,'{"sessionId":"pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:3',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,3,'message.changed','message','transient',1,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:4',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,4,'message.changed','message','inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:5',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,5,'message.changed','message','uncertain',1,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:6',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,6,'message.changed','message','unavailable',1,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:7',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,7,'message.changed','message','unknown-unavailable',1,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:8',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,8,'message.changed','message','failed',1,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:9',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,9,'message.changed','message','terminal-pending',1,1000,0,'{"sessionId":"terminal-pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:10',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,10,'message.changed','message','terminal-delivered_to_turn',1,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:11',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,11,'message.changed','message','terminal-next_turn',1,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:12',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,12,'message.changed','message','terminal-unsupported',1,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:13',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,13,'message.changed','message','terminal-failed',1,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:14',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,14,'effect.changed','effect','send/transient',1,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:15',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,15,'effect.changed','effect','send/inflight',1,1000,0,'{"sessionId":"inflight","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:16',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,16,'effect.changed','effect','send/uncertain',1,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:17',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,17,'effect.changed','effect','send/unavailable',1,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:18',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,18,'effect.changed','effect','send/unknown-unavailable',1,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:19',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,19,'effect.changed','effect','send/failed',1,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:20',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,20,'effect.changed','effect','send/terminal-delivered_to_turn',1,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:21',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,21,'effect.changed','effect','send/terminal-next_turn',1,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:22',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,22,'effect.changed','effect','send/terminal-unsupported',1,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:23',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,23,'effect.changed','effect','send/terminal-failed',1,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:24',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,24,'effect.changed','effect','send/transient',2,1000,0,'{"sessionId":"transient","runId":null,"stepId":null,"parentId":null,"state":"pending","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:25',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,25,'effect.changed','effect','send/uncertain',2,1000,0,'{"sessionId":"uncertain","runId":null,"stepId":null,"parentId":null,"state":"uncertain","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:26',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,26,'effect.changed','effect','send/unavailable',2,1000,0,'{"sessionId":"unavailable","runId":null,"stepId":null,"parentId":null,"state":"unavailable","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:27',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,27,'effect.changed','effect','send/unknown-unavailable',2,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"uncertain","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:28',3,'e3110336057f40b192365dc9ace5cb8f',2000,2000,NULL,28,'effect.changed','effect','send/unknown-unavailable',3,2000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"inflight","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:29',4,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,29,'effect.changed','effect','send/unknown-unavailable',4,1000,0,'{"sessionId":"unknown-unavailable","runId":null,"stepId":null,"parentId":null,"state":"unavailable","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:30',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,30,'effect.changed','effect','send/failed',2,1000,0,'{"sessionId":"failed","runId":null,"stepId":null,"parentId":null,"state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:31',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,31,'message.changed','message','terminal-delivered_to_turn',2,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"delivered_to_turn","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:32',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,32,'effect.changed','effect','send/terminal-delivered_to_turn',2,1000,0,'{"sessionId":"terminal-delivered_to_turn","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:33',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,33,'message.changed','message','terminal-next_turn',2,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"next_turn","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:34',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,34,'effect.changed','effect','send/terminal-next_turn',2,1000,0,'{"sessionId":"terminal-next_turn","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:35',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,35,'message.changed','message','terminal-unsupported',2,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"unsupported","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:36',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,36,'effect.changed','effect','send/terminal-unsupported',2,1000,0,'{"sessionId":"terminal-unsupported","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:37',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,37,'message.changed','message','terminal-failed',2,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:38',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,38,'effect.changed','effect','send/terminal-failed',2,1000,0,'{"sessionId":"terminal-failed","runId":null,"stepId":null,"parentId":null,"state":"done","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:39',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,39,'message.changed','message','terminal-pending',2,1000,0,'{"sessionId":"terminal-pending","runId":null,"stepId":null,"parentId":null,"state":"unsupported","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:40',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,40,'message.changed','message','held',1,1000,0,'{"sessionId":"held","runId":null,"stepId":null,"parentId":null,"state":"held","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:41',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,41,'session.cancel_requested','session','held',1,1000,0,'{"sessionId":"held","runId":null,"stepId":null,"parentId":null,"state":"idle","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:42',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,42,'session.changed','session','child/x',1,1000,0,'{"sessionId":"child/x","runId":null,"stepId":null,"parentId":"child-parent","state":"ended","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:43',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,43,'message.changed','message','child-ended/child/x',1,1000,0,'{"sessionId":"child-parent","runId":null,"stepId":null,"parentId":"child/x","state":"queued","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:44',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,44,'step.changed','step','history-step',1,1000,0,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"running","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:45',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,45,'run.changed','run','history-run',1,1000,0,'{"sessionId":"pending","runId":"history-run","stepId":null,"parentId":null,"state":"running","epoch":null}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:46',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,46,'step.changed','step','history-step',2,1000,0,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:47',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,47,'run.changed','run','history-run',2,1000,0,'{"sessionId":"pending","runId":"history-run","stepId":null,"parentId":null,"state":"failed","epoch":null}');
INSERT INTO "engine_event" VALUES('e3110336057f40b192365dc9ace5cb8f:48',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,48,'step.late','step','history-step',2,1000,1,'{"sessionId":"history-worker","runId":"history-run","stepId":"history-step","parentId":"pending","state":"failed","epoch":0}');
INSERT INTO "engine_event" VALUES('remote:1',1,'remote',1000,1000,NULL,1,'message.changed','message','pending',1,1000,0,'{"sessionId":"pending","runId":null,"stepId":null,"parentId":null,"state":"queued","epoch":0}');
INSERT INTO "engine_message" VALUES('pending',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','pending',NULL,NULL,'body pending','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('pending-2',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','pending',NULL,NULL,'second','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('transient',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','transient',NULL,NULL,'body transient','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('inflight',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','inflight',NULL,NULL,'body inflight','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('uncertain',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','uncertain',NULL,NULL,'body uncertain','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('unavailable',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','unavailable',NULL,NULL,'body unavailable','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('unknown-unavailable',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','unknown-unavailable',NULL,NULL,'body unknown-unavailable','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('failed',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','failed',NULL,NULL,'body failed','prompt','queued',NULL,NULL);
INSERT INTO "engine_message" VALUES('terminal-pending',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','terminal-pending',NULL,NULL,'body terminal-pending','prompt','unsupported','old-turn','historical');
INSERT INTO "engine_message" VALUES('terminal-delivered_to_turn',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','terminal-delivered_to_turn',NULL,NULL,'body terminal-delivered_to_turn','prompt','delivered_to_turn','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-next_turn',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','terminal-next_turn',NULL,NULL,'body terminal-next_turn','prompt','next_turn','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-unsupported',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','terminal-unsupported',NULL,NULL,'body terminal-unsupported','prompt','unsupported','old-turn',NULL);
INSERT INTO "engine_message" VALUES('terminal-failed',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','terminal-failed',NULL,NULL,'body terminal-failed','prompt','failed','old-turn',NULL);
INSERT INTO "engine_message" VALUES('held',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,NULL,'window','held',NULL,NULL,'interrupt','interrupt','held',NULL,NULL);
INSERT INTO "engine_message" VALUES('child-ended/child/x',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'child/x','system','child-parent',NULL,NULL,'Child session child/x ended.','prompt','queued',NULL,NULL);
INSERT INTO "engine_meta" VALUES(1,2,'e3110336057f40b192365dc9ace5cb8f',49,1,2);
INSERT INTO "engine_mirror" VALUES('remote','Remote',7,4,'{}',2,'live',1000);
INSERT INTO "engine_mirror_row" VALUES('remote','session','remote-session',7,3,'{}');
INSERT INTO "engine_outbox" VALUES('send/pending',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,1,'pending',NULL,'send','{"fromSessionId":null,"toSessionId":"pending","text":"body pending","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/pending-2',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,2,'pending',NULL,'send','{"fromSessionId":null,"toSessionId":"pending","text":"second","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/transient',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,3,'transient',NULL,'send','{"fromSessionId":null,"toSessionId":"transient","text":"body transient","mode":"prompt"}','pending',1,2000,'{"disposition":1,"data":null,"error":"retry"}','retry');
INSERT INTO "engine_outbox" VALUES('send/inflight',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,4,'inflight',NULL,'send','{"fromSessionId":null,"toSessionId":"inflight","text":"body inflight","mode":"prompt"}','inflight',1,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/uncertain',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,5,'uncertain',NULL,'send','{"fromSessionId":null,"toSessionId":"uncertain","text":"body uncertain","mode":"prompt"}','uncertain',1,NULL,'{"disposition":4,"data":null,"error":"interrupted"}','interrupted');
INSERT INTO "engine_outbox" VALUES('send/unavailable',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,6,'unavailable',NULL,'send','{"fromSessionId":null,"toSessionId":"unavailable","text":"body unavailable","mode":"prompt"}','unavailable',1,NULL,'{"disposition":2,"data":null,"error":"offline"}','offline');
INSERT INTO "engine_outbox" VALUES('send/unknown-unavailable',4,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,7,'unknown-unavailable',NULL,'send','{"fromSessionId":null,"toSessionId":"unknown-unavailable","text":"body unknown-unavailable","mode":"prompt"}','unavailable',2,NULL,'{"disposition":4,"data":null,"error":"offline"}','offline');
INSERT INTO "engine_outbox" VALUES('send/failed',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,8,'failed',NULL,'send','{"fromSessionId":null,"toSessionId":"failed","text":"body failed","mode":"prompt"}','failed',1,NULL,'{"disposition":3,"data":null,"error":"permanent"}','permanent');
INSERT INTO "engine_outbox" VALUES('send/terminal-pending',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,9,'terminal-pending',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-pending","text":"body terminal-pending","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-delivered_to_turn',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,10,'terminal-delivered_to_turn',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-delivered_to_turn","text":"body terminal-delivered_to_turn","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"delivered_to_turn","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-next_turn',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,11,'terminal-next_turn',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-next_turn","text":"body terminal-next_turn","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"next_turn","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-unsupported',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,12,'terminal-unsupported',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-unsupported","text":"body terminal-unsupported","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"unsupported","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('send/terminal-failed',2,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,13,'terminal-failed',NULL,'send','{"fromSessionId":null,"toSessionId":"terminal-failed","text":"body terminal-failed","mode":"prompt"}','done',1,NULL,'{"disposition":0,"data":{"state":"failed","providerTurnId":"old-turn","reason":null},"error":null}',NULL);
INSERT INTO "engine_outbox" VALUES('message-stop/held',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,41,'held',NULL,'stop','{}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_outbox" VALUES('send/child-ended/child/x',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,43,'child-parent',NULL,'send','{"fromSessionId":"child/x","toSessionId":"child-parent","text":"Child session child/x ended.","mode":"prompt"}','pending',0,NULL,NULL,NULL);
INSERT INTO "engine_run" VALUES('history-run',2,'e3110336057f40b192365dc9ace5cb8f',2000,1000,NULL,'pending',NULL,NULL,NULL,'failed',60000,NULL);
INSERT INTO "engine_session" VALUES('pending',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,'history-run',NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('transient',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('inflight',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('uncertain',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('unavailable',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('unknown-unavailable',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('failed',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('held',1,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,1,NULL);
INSERT INTO "engine_session" VALUES('terminal-pending',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-delivered_to_turn',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-next_turn',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-unsupported',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('terminal-failed',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('child-parent',0,'e3110336057f40b192365dc9ace5cb8f',1000,1000,NULL,'codex','model','/test','Test','idle','idle',NULL,NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('child/x',1,'e3110336057f40b192365dc9ace5cb8f',2000,1000,NULL,'codex','model','/test','Test','ended','idle','child-parent',NULL,NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_session" VALUES('history-worker',0,'e3110336057f40b192365dc9ace5cb8f',2000,2000,NULL,'codex','model','/test','Test','idle','idle','pending','history-run',NULL,'local',0,NULL,0,NULL);
INSERT INTO "engine_step" VALUES('history-step',2,'e3110336057f40b192365dc9ace5cb8f',2000,1000,NULL,'history-run','history-step','history-worker','failed',1,1,1,0,1000,1000,'[]','old failure');
INSERT INTO "engine_step_attempt" VALUES('history-step',1,'failed',1000,1000,'[]','late failure',1);
INSERT INTO "engine_step_fact" VALUES('00000027000000000000000000000000','history-step',1,'failed',1000,'{"state":"failed","attempt":1,"proofUrls":[],"reason":"late failure","byWorkflowRule":false}',1);
INSERT INTO "rows" VALUES('old-chat',1,'turn','old-chat','message','{"blocks":[]}',1000);
INSERT INTO "sessions" VALUES('old-chat',2);
CREATE INDEX engine_event_machine_seq ON engine_event(machine,seq);
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
