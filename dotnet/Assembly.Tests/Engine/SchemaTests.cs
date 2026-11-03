using Assembly.Engine.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Engine;

public sealed class SchemaTests
{
    [Fact]
    public void Installing_engine_preserves_existing_chat_rows()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var chat = RowStore.Open(db);
        using var payload = JsonDocument.Parse("{\"blocks\":[]}");
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("turn", "message", "message", payload.RootElement.Clone(), 1000));
        var before = Assert.Single(chat.Commit("existing", batch));

        EngineSchema.Open(db, 1000);

        var after = Assert.Single(chat.RowsAfter("existing", 0));
        Assert.Equal(before.Seq, after.Seq);
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(before.Payload.GetRawText(), after.Payload.GetRawText());
        Assert.Equal(1, chat.LastSeq("existing"));
    }

    [Fact]
    public void Reopen_keeps_machine_and_chat_format()
    {
        using var f = new EngineFixture();
        var machineBefore = f.Machine;
        f.Db.Writer.Write("test.chat", s => s.Execute("""
            INSERT INTO sessions(session_id,next_seq) VALUES('chat',2);
            INSERT INTO rows(session_id,seq,run_id,item_id,kind,payload,created_at)
            VALUES('chat',1,'turn','message','message','{"blocks":[]}',1000);
            """));
        f.SeedSession();
        f.SeedRun();
        f.SeedStep();
        f.Db.Writer.Write("test.cursor", s => s.Execute("UPDATE engine_meta SET next_seq=12,retained_after=3,snapshot_seq=8"));
        f.Reopen();
        Assert.Equal(machineBefore, f.Machine);
        Assert.Equal(5L, f.Db.QueryFirst("PRAGMA user_version", r => r.Long(0), default));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal("message", Assert.Single(RowStore.Open(f.Db).RowsAfter("chat", 0)).ItemId);
        Assert.Equal(12, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Equal(3, f.Number("SELECT retained_after FROM engine_meta"));
        Assert.Equal(8, f.Number("SELECT snapshot_seq FROM engine_meta"));
        Assert.Equal("r", f.Text("SELECT run_id FROM engine_session WHERE id='s'"));
        Assert.Equal("pending", f.State("engine_step", "step"));
        Assert.Equal(0, f.Number("SELECT version FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public void Invalid_link_rolls_back_the_whole_write()
    {
        using var f = new EngineFixture();
        Assert.Throws<SqliteException>(() => f.Db.Writer.Write("test.invalid-link", scope =>
        {
            scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                    activity_state,holder,epoch,cancel_requested)
                VALUES ('session',0,'local',1000,1000,'codex','model','/test','Test','idle','idle','local',0,0);
                """);
            return scope.Execute("""
                INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,run_id,name,state,attempt,max_attempts,required,rule_skippable)
                VALUES ('step',0,'local',1000,1000,'missing','Test','pending',0,1,1,0);
                """);
        }));
        Assert.Equal(0, f.Count("engine_session"));
        Assert.Equal(0, f.Count("engine_step"));
    }

    [Fact]
    public void Version_13_refuses()
    {
        using var f = new EngineFixture();
        f.Db.Writer.Write("test.newer-schema", s => s.Execute("UPDATE engine_meta SET schema_version=13"));
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(f.Db, 1000));
        Assert.Equal(13, f.Db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
    }

    [Fact]
    public void Generated_event_json_round_trips_normalized_data()
    {
        var item = new EngineEvent("machine", 1, "changed", "session", "s", 2, 1000, true,
            new EngineEventData("s", "r", "step", null, "idle", 3));
        var json = JsonSerializer.Serialize(item, EngineJson.Default.EngineEvent);
        Assert.Equal(item, JsonSerializer.Deserialize(json, EngineJson.Default.EngineEvent));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("s", document.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
    }

    [Fact]
    public void Recording_identity_and_json_constraints_reject_duplicates_and_invalid_json()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        void Need(string id, string? request, string json = "[]") => f.Db.Writer.Write("test.need", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,provider_request_id,state)
            VALUES($id,0,'local',1000,1000,'s','permission','Allow?',$json,$request,'pending')
            """, new("$id", id), new("$json", json), new("$request", request)));
        Need("n1", "request");
        Assert.Throws<SqliteException>(() => Need("n2", "request"));
        Need("n3", null);
        Need("n4", null);
        Assert.Throws<SqliteException>(() => Need("bad", null, "invalid"));
        void Tool(string id) => f.Db.Writer.Write("test.tool", s => s.Execute("""
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,epoch,boundary,boundary_id,
                tool,input_bytes,started_ms,late)
            VALUES($id,0,'local',1000,1000,'s',0,'provider','call','read',1,1000,0)
            """, new SqlArg("$id", id)));
        Tool("t1");
        Assert.Throws<SqliteException>(() => Tool("t2"));
        void Usage(string id) => f.Db.Writer.Write("test.usage", s => s.Execute("""
            INSERT INTO engine_usage(id,version,machine,created_ms,updated_ms,session_id,epoch,provider_turn_id,late)
            VALUES($id,0,'local',1000,1000,'s',0,'turn',0)
            """, new SqlArg("$id", id)));
        Usage("u1");
        Assert.Throws<SqliteException>(() => Usage("u2"));
    }

    [Fact]
    public void Event_removal_keeps_effects_attempts_facts_and_answered_records()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        f.SeedRun();
        f.SeedStep();
        f.Db.Writer.Write("test.history", s => s.Execute("""
            INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
            VALUES('e',0,'local',1000,1000,1,'changed','step','step',0,1000,0,'{}');
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,run_id,kind,payload_json,state,attempt)
            VALUES('effect',0,'local',1000,1000,1,'s','r','start','{}','pending',0);
            INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,ended_ms,proof_json,reason,late)
            VALUES('step',1,'completed',1000,1001,'{}',NULL,0);
            INSERT INTO engine_step_fact(id,step_id,attempt,kind,at_ms,data_json,late)
            VALUES('fact','step',1,'outcome',1001,'{}',1);
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,answer_json)
            VALUES('need',0,'local',1000,1000,'s','permission','Allow?','[]','answered','{}');
            """));
        Assert.Equal(1, Assert.Single(f.Events()).Seq);
        f.Db.Writer.Write("test.retention", s => s.Execute("DELETE FROM engine_event"));
        f.Reopen();
        Assert.Empty(f.Events());
        Assert.Equal("effect", f.Text("SELECT id FROM engine_outbox"));
        Assert.Equal(1, f.Count("engine_step_attempt"));
        Assert.Equal(1, f.Count("engine_step_fact"));
        Assert.Equal("answered", f.State("engine_needs_me", "need"));
    }

    [Fact]
    public void Version_1_upgrades_in_place()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        SeedVersion1(db);
        var originalColumns = OriginalColumns(db);
        var before = StoredRows(db, originalColumns);
        var indexes = db.Query("SELECT name FROM sqlite_master WHERE type='index' AND sql IS NOT NULL ORDER BY name", r => r.Text(0), default);

        Assert.Equal("original-machine", EngineSchema.Open(db, 2000));

        Assert.Equal(12, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(before, StoredRows(db, originalColumns));
        Assert.Equal("s", db.QueryFirst("SELECT order_key FROM engine_outbox WHERE id='effect'", r => r.Text(0), default));
        Assert.Null(db.QueryFirst("SELECT executor_json FROM engine_outbox WHERE id='effect'", r => r.IsNull(0) ? null : r.Text(0), default));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE source_kind!='provider' OR execution_json IS NOT NULL", r => r.Long(0), default));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM engine_run WHERE setup_state!='ready' OR paused!=0", r => r.Long(0), default));
        Assert.Equal(new long[] { 1, 2, 3 }, db.Query("SELECT seq FROM engine_event ORDER BY seq", r => r.Long(0), default));
        Assert.Equal(new long[] { 4, 1, 2 }, db.QueryFirst("SELECT next_seq,retained_after,snapshot_seq FROM engine_meta", r => new[] { r.Long(0), r.Long(1), r.Long(2) }, default));
        Assert.All(indexes, name => Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name", r => r.Long(0), default, new SqlArg("$name", name))));
        Assert.Empty(db.Query("PRAGMA foreign_key_check", r => r.Text(0), default));
        Assert.Equal("original-machine", EngineSchema.Open(db, 3000));
        Assert.Equal(before, StoredRows(db, originalColumns));
        Assert.Throws<SqliteException>(() => db.Writer.Write("invalid.event", s => s.Execute("UPDATE engine_event SET data_json='invalid' WHERE seq=1")));
        Assert.Throws<SqliteException>(() => db.Writer.Write("duplicate.event", s => s.Execute("UPDATE engine_event SET seq=1 WHERE seq=2")));
        Assert.Throws<SqliteException>(() => db.Writer.Write("invalid.link", s => s.Execute("UPDATE engine_step SET run_id='missing'")));
    }

    [Fact]
    public void Failed_upgrade_rolls_back_schema_rows_and_version()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        SeedVersion1(db);
        // A late DDL failure must undo the event table replacement as well as the version change.
        db.Writer.Write("test.block-upgrade", s => s.Execute("CREATE TABLE engine_mirror_row(blocker TEXT)"));
        var originalColumns = OriginalColumns(db);
        var before = StoredRows(db, originalColumns);
        var schema = db.Query("SELECT sql FROM sqlite_master ORDER BY name", r => r.IsNull(0) ? null : r.Text(0), default);
        Assert.Throws<SqliteException>(() => EngineSchema.Open(db, 2000));
        Assert.Equal(1, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(before, StoredRows(db, originalColumns));
        Assert.Equal(schema, db.Query("SELECT sql FROM sqlite_master ORDER BY name", r => r.IsNull(0) ? null : r.Text(0), default));
        db.Writer.Write("test.unblock-upgrade", s => s.Execute("DROP TABLE engine_mirror_row"));
        Assert.Equal("original-machine", EngineSchema.Open(db, 2000));
    }

    [Fact]
    public void Mirror_tables_enforce_identity_state_json_and_parent()
    {
        using var f = new EngineFixture();
        void Write(string sql) => f.Db.Writer.Write("test.mirror-schema", s => s.Execute(sql));
        Write("INSERT INTO engine_mirror VALUES('remote','Workbox',1,3,NULL,0,'snapshotting',1000)");
        Write("INSERT INTO engine_mirror_row VALUES('remote','run','r',1,2,'{}')");
        Write("INSERT INTO engine_mirror_row VALUES('remote','run','r',2,3,'{}')");
        Assert.Throws<SqliteException>(() => Write("INSERT INTO engine_mirror_row VALUES('remote','run','r',1,2,'{}')"));
        Assert.Throws<SqliteException>(() => Write("INSERT INTO engine_mirror_row VALUES('unknown','run','r',1,2,'{}')"));
        Assert.Throws<SqliteException>(() => Write("UPDATE engine_mirror_row SET row_json='bad'"));
        Assert.Throws<SqliteException>(() => Write("UPDATE engine_mirror SET at_json='bad'"));
        Assert.Throws<SqliteException>(() => Write("UPDATE engine_mirror SET state='unknown'"));
        Write("UPDATE engine_mirror SET state='live',at_json='{}'");
        Write("UPDATE engine_mirror SET state='retired'");
        Assert.Equal(2, f.Count("engine_mirror_row"));
    }

    private static IReadOnlyDictionary<string, string[]> OriginalColumns(IAppDatabase db) => db.Query(
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'engine_%' AND name NOT IN ('engine_meta','engine_mirror','engine_mirror_row') ORDER BY name",
        r => r.Text(0), default).ToDictionary(table => table, table => db.Query($"PRAGMA table_info({table})", r => r.Text(1), default)
            .OrderBy(column => column, StringComparer.Ordinal).ToArray());

    private static string[] StoredRows(IAppDatabase db, IReadOnlyDictionary<string, string[]> originalColumns) =>
        originalColumns.SelectMany(pair => db.Query($"SELECT json_array({string.Join(',', pair.Value)}) FROM {pair.Key} ORDER BY 1",
            r => pair.Key + ":" + r.Text(0), default)).ToArray();

    private static void SeedVersion1(IAppDatabase db) => db.Writer.Write("test.actual-v1", s =>
    {
        s.Execute(Version1);
        s.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,activity_state,holder,epoch,cancel_requested)
            VALUES('s',2,'original-machine',10,20,'codex','model','/test','Original','idle','idle','original-machine',0,0);
            INSERT INTO engine_run(id,version,machine,created_ms,updated_ms,owner_session_id,state,owner_loss_limit_ms)
            VALUES('r',1,'original-machine',10,20,'s','succeeded',60000);
            UPDATE engine_session SET run_id='r';
            INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,run_id,name,state,attempt,max_attempts,required,rule_skippable)
            VALUES('step',1,'original-machine',10,20,'r','Original step','succeeded',1,1,1,0);
            INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,run_id,name,state,attempt,max_attempts,required,rule_skippable)
            VALUES('next',0,'original-machine',10,20,'r','Next step','pending',0,1,1,0);
            INSERT INTO engine_step_dependency VALUES('next','step',0);
            INSERT INTO engine_step_attempt VALUES('step',1,'succeeded',10,20,'{}','done',0);
            INSERT INTO engine_step_fact VALUES('fact','step',1,'outcome',20,'{}',0);
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,answer_json,provider_request_id)
            VALUES('need',1,'original-machine',10,20,'s','question','Keep?','[]','answered','{"yes":true}','request');
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state)
            VALUES('message',1,'original-machine',10,20,'window','s','Original message','queue','delivered');
            INSERT INTO engine_handoff(id,version,machine,created_ms,updated_ms,session_id,old_holder,new_holder,old_epoch,next_epoch,state)
            VALUES('handoff',1,'original-machine',10,20,'s','original-machine','target',0,1,'failed');
            INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,run_id,repo,state)
            VALUES('pr',1,'original-machine',10,20,'r','repo','merged');
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,epoch,boundary,boundary_id,tool,input_preview,input_bytes,started_ms,late)
            VALUES('tool',1,'original-machine',10,20,'s',0,'provider','call','read','Object(1)',12,10,0);
            INSERT INTO engine_usage(id,version,machine,created_ms,updated_ms,session_id,epoch,provider_turn_id,input_tokens,late)
            VALUES('usage',1,'original-machine',10,20,'s',0,'turn',12,0);
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,receipt_json)
            VALUES('effect',1,'original-machine',10,20,3,'s','send','{}','done',1,'{"ok":true}');
            INSERT INTO engine_command_receipt VALUES('receipt','fingerprint','{"applied":true}');
            WITH numbers(n) AS (VALUES(1),(2),(3))
            INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,task_url,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
            SELECT 'original-machine:'||n,n,'original-machine',10,20,'https://example.com/task',n,'changed','run','r',n,20,0,'{"runId":"r"}' FROM numbers;
            """);
        return 0;
    });

    // Frozen v1 DDL from accepted producer 13d68677; never generated from the schema under test.
    private const string Version1 = """
        CREATE TABLE engine_meta (
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1), schema_version INTEGER NOT NULL,
            machine_id TEXT NOT NULL, next_seq INTEGER NOT NULL, retained_after INTEGER NOT NULL,
            snapshot_seq INTEGER NOT NULL);
        CREATE TABLE engine_session (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            provider TEXT NOT NULL, model TEXT NOT NULL, folder TEXT NOT NULL, title TEXT NOT NULL,
            state TEXT NOT NULL, activity_state TEXT NOT NULL,
            parent_id TEXT REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            provider_session_id TEXT, holder TEXT NOT NULL, epoch INTEGER NOT NULL,
            reserved_epoch INTEGER, cancel_requested INTEGER NOT NULL, lost_ms INTEGER);
        CREATE TABLE engine_run (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            owner_session_id TEXT NOT NULL REFERENCES engine_session(id), worktree TEXT, branch TEXT,
            pr_id TEXT REFERENCES engine_pr(id), state TEXT NOT NULL, owner_loss_limit_ms INTEGER NOT NULL,
            owner_lost_ms INTEGER);
        CREATE TABLE engine_step (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            run_id TEXT NOT NULL REFERENCES engine_run(id), name TEXT NOT NULL,
            session_id TEXT REFERENCES engine_session(id), state TEXT NOT NULL, attempt INTEGER NOT NULL,
            max_attempts INTEGER NOT NULL, required INTEGER NOT NULL, rule_skippable INTEGER NOT NULL,
            started_ms INTEGER, ended_ms INTEGER, proof_json TEXT CHECK(proof_json IS NULL OR json_valid(proof_json)),
            outcome_reason TEXT);
        CREATE TABLE engine_needs_me (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), kind TEXT NOT NULL, text TEXT NOT NULL,
            options_json TEXT NOT NULL CHECK(json_valid(options_json)), provider_request_id TEXT, chat_row_id TEXT,
            state TEXT NOT NULL, deadline_ms INTEGER,
            answer_json TEXT CHECK(answer_json IS NULL OR json_valid(answer_json)), answered_by TEXT, answered_ms INTEGER);
        CREATE TABLE engine_message (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            from_session_id TEXT REFERENCES engine_session(id), sender_kind TEXT NOT NULL,
            to_session_id TEXT NOT NULL REFERENCES engine_session(id), run_id TEXT REFERENCES engine_run(id),
            step_id TEXT REFERENCES engine_step(id), text TEXT NOT NULL, mode TEXT NOT NULL, state TEXT NOT NULL,
            provider_turn_id TEXT, reason TEXT);
        CREATE TABLE engine_handoff (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            session_id TEXT NOT NULL REFERENCES engine_session(id), old_holder TEXT NOT NULL, new_holder TEXT NOT NULL,
            old_epoch INTEGER NOT NULL, next_epoch INTEGER NOT NULL, state TEXT NOT NULL, reason TEXT);
        CREATE TABLE engine_pr (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            run_id TEXT NOT NULL REFERENCES engine_run(id), repo TEXT NOT NULL, number INTEGER, head TEXT,
            state TEXT NOT NULL, reported_by TEXT, reported_state TEXT, observed_head TEXT, observed_ms INTEGER, url TEXT);
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
        CREATE TABLE engine_event (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
            seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL,
            entity_version INTEGER NOT NULL, at_ms INTEGER NOT NULL, late INTEGER NOT NULL,
            data_json TEXT NOT NULL CHECK(json_valid(data_json)));
        -- event_seq is provenance: events may be compacted while an effect remains durable.
        CREATE TABLE engine_outbox (id TEXT PRIMARY KEY, version INTEGER NOT NULL, machine TEXT NOT NULL,
        created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, task_url TEXT NULL,
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
        INSERT INTO engine_meta VALUES(1,1,'original-machine',4,1,2);
        """;
}
