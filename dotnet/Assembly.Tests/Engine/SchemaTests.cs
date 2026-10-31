using Assembly.Application.Features.Engine.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
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
        Assert.Equal(4L, f.Db.QueryFirst("PRAGMA user_version", r => r.Long(0), default));
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
    public void Unsupported_schema_does_not_serve()
    {
        using var f = new EngineFixture();
        f.Db.Writer.Write("test.newer-schema", s => s.Execute("UPDATE engine_meta SET schema_version=2"));
        Assert.Throws<InvalidOperationException>(() => EngineSchema.Open(f.Db, 1000));
        Assert.Equal(2, f.Db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
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
}
