using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using static Assembly.Application.Features.Chat.Sessions.SessionsFeature;

namespace Assembly.Tests.Draft.SessionsList;

public sealed partial class SessionRailTests
{
    [Fact]
    public void Engine_needs_count_items_before_paging_and_chat_asks_do_not_count()
    {
        var codex = Parent("codex");
        var claude = Parent("claude");
        OpenEngineNeed(codex, "codex-a");
        OpenEngineNeed(codex, "codex-b");
        OpenEngineNeed(claude, "claude-a");
        Rows(claude, ("raw-ask", RowKind.Ask, new { state = "open" }));
        Assert.Equal(3, Read(limit: 1).Totals.Needs);
        Assert.Equal(2, Read(providers: ["codex"], limit: 1).Totals.Needs);
        Assert.Equal(1, Read(providers: ["claude"]).Totals.Needs);
        _database.Writer.Write("test.answer", scope =>
        {
            var context = new CommandContext(Guid.NewGuid(), _engineMachine, new(ActorKind.Window, "window"), "codex-a", 1, 0, 1_000_000);
            var result = new EngineCommands(_engineMachine, new(() => 1, () => 5)).Apply(scope, context,
                new ResolveNeed("answered", JsonSerializer.SerializeToElement("Recorded answer")));
            Assert.True(result.Applied, result.Error);
            return 0;
        });
        Assert.Equal(1, Read(providers: ["codex"]).Totals.Needs);
    }

    [Fact]
    public void Current_generation_and_exact_owner_are_required_for_remote_needs_and_metadata()
    {
        var remote = Parent("codex", machine: "workbox-test");
        OpenEngineNeed(remote, "remote-a");
        Assert.Equal(1, Read(locations: ["remote"]).Totals.Needs);
        Assert.Equal("recorded-model", Entry(Read(), remote).Model);
        Assert.Equal(new ExpectedSource("fixture-engine:workbox-test", 1, "workbox-test"), Entry(Read(), remote).Source);
        _database.Writer.Write("test.replace-source", scope => scope.Execute(
            "UPDATE engine_mirror SET generation=2 WHERE name='workbox-test'"));
        Assert.Equal(0, Read(locations: ["remote"]).Totals.Needs);
        Assert.Null(Entry(Read(), remote).Model);
        Assert.Null(Entry(Read(), remote).Source);
        _database.Writer.Write("test.restore-contradiction", scope => scope.Execute("""
            UPDATE engine_mirror SET generation=1 WHERE name='workbox-test';
            UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.machine','different-owner') WHERE entity_kind='session';
            """));
        Assert.Equal(0, Read().Totals.Needs);
        Assert.Null(Entry(Read(), remote).Model);
        Assert.Null(Entry(Read(), remote).Source);
    }

    [Fact]
    public void Rail_metadata_uses_engine_parent_and_recorded_run_without_changing_ordinary_order()
    {
        var parent = Parent();
        var child = Helper(parent);
        _database.Writer.Write("test.metadata", scope => scope.Execute("""
            INSERT INTO engine_run(id,version,machine,created_ms,updated_ms,owner_session_id,worktree,state,owner_loss_limit_ms)
            VALUES('recorded-run',1,$machine,1,1,$child,'/recorded/worktree','running',1000);
            UPDATE engine_session SET model='selected-model',run_id='recorded-run' WHERE id=$child;
            UPDATE session_info SET pinned_at=10,orchestrator_at=20,position=3 WHERE session_id=$child;
            """, new("$machine", _engineMachine), new("$child", child)));
        Rows(child, (null, RowKind.Error, JsonSerializer.SerializeToElement(
            new ErrorPayload("First line\nSecond line\r" + new string('x', 300), "failed"), RowJsonContext.Rows.ErrorPayload)));
        var entry = Entry(Read(), child);
        Assert.Equal(new ExpectedSource(_engineMachine, 0, null), entry.Source);
        Assert.Equal(parent, entry.ParentSessionId);
        Assert.Equal(1, entry.Depth);
        Assert.Equal(0, Entry(Read(), parent).Depth);
        Assert.Equal((10L, 20L, 3L), (entry.PinnedAt, entry.OrchestratorAt, entry.Position));
        Assert.Equal("selected-model", entry.Model);
        Assert.Equal("/recorded/worktree", entry.Worktree);
        Assert.NotNull(entry.LastError);
        Assert.Equal(200, entry.LastError.Length);
        Assert.DoesNotContain('\n', entry.LastError);
        Assert.DoesNotContain('\r', entry.LastError);
        Assert.Null(entry.ResumeCommand);
        Assert.Null(entry.LastSeenMs);
        Assert.Equal([child, parent], Sessions(Read(groupBy: new(false, false))));
        SessionStore.Open(_database, _time, "checkouts");
        Assert.Equal((entry.PinnedAt, entry.OrchestratorAt, entry.Position),
            (Entry(Read(), child).PinnedAt, Entry(Read(), child).OrchestratorAt, Entry(Read(), child).Position));
    }

    [Fact]
    public void Rail_wire_has_canonical_source_and_honest_nullable_metadata_without_chat_ask_authority()
    {
        var session = Parent();
        Rows(session, ("raw-ask", RowKind.Ask, new { state = "open" }));
        var wire = JsonSerializer.SerializeToElement(Read(groupBy: new(false, false)), SessionsJson.Default.Rail);
        var entry = Assert.Single(wire.GetProperty("sessions").EnumerateArray());
        Assert.Equal(_engineMachine, entry.GetProperty("source").GetProperty("machine").GetString());
        Assert.Equal(0, entry.GetProperty("source").GetProperty("generation").GetInt64());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("source").GetProperty("routeMachine").ValueKind);
        foreach (var field in new[] { "pinnedAt", "orchestratorAt", "position", "worktree", "resumeCommand", "lastSeenMs" })
            Assert.Equal(JsonValueKind.Null, entry.GetProperty(field).ValueKind);
        foreach (var field in new[] { "needsYou", "askSession", "askSeq" }) Assert.False(entry.TryGetProperty(field, out _));
        Assert.Equal(0, wire.GetProperty("totals").GetProperty("needs").GetInt32());
    }

    private void RecordEngineSession(string session, string? parent = null)
    {
        var facade = _sessions.Get(session)!;
        _database.Writer.Write("test.engine-session", scope =>
        {
            if (facade.Machine is { } route)
            {
                var machine = "fixture-engine:" + route;
                scope.Execute("""
                    INSERT OR IGNORE INTO engine_mirror(machine,name,generation,through,retained_after,state,updated_ms)
                    VALUES($machine,$route,1,0,0,'live',1);
                    INSERT INTO engine_mirror_row(machine,entity_kind,entity_id,generation,version,row_json)
                    VALUES($machine,'session',$session,1,1,$json);
                    """, new("$machine", machine), new("$route", route), new("$session", session),
                    new("$json", JsonSerializer.Serialize(new { id = session, machine, holder = "local", state = "idle", parentId = parent, model = "recorded-model" })));
            }
            else scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                    activity_state,parent_id,holder,epoch,cancel_requested)
                VALUES($session,1,$machine,1,1,$provider,'recorded-model','/recorded/folder','Recorded','idle','idle',$parent,'local',0,0);
                """, new("$session", session), new("$machine", _engineMachine), new("$provider", facade.Agent), new("$parent", parent));
            return 0;
        });
    }

    private void OpenEngineNeed(string session, string id)
    {
        var facade = _sessions.Get(session)!;
        _database.Writer.Write("test.engine-need", scope =>
        {
            if (facade.Machine is { } route)
            {
                var machine = "fixture-engine:" + route;
                scope.Execute("""
                    INSERT INTO engine_mirror_row(machine,entity_kind,entity_id,generation,version,row_json)
                    VALUES($machine,'needs_me',$id,1,1,$json);
                    """, new("$machine", machine), new("$id", id),
                    new("$json", JsonSerializer.Serialize(new { id, machine, sessionId = session, state = "open" })));
            }
            else
            {
                var actor = facade.ParentSessionId is null ? new EngineActor(ActorKind.Session, session) : new(ActorKind.System, "adapter");
                var context = new CommandContext(Guid.NewGuid(), _engineMachine, actor, id, 0, 0, 1_000_000);
                var result = new EngineCommands(_engineMachine, new(() => 1, () => 5)).Apply(scope, context,
                    new OpenNeed(session, null, null, "question", "Recorded Engine question", [], null, null, null));
                Assert.True(result.Applied, result.Error);
            }
            return 0;
        });
    }
}
