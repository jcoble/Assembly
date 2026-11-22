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
    public void Current_generation_and_exact_owner_are_required_for_remote_needs_and_metadata()
    {
        var remote = Parent("codex", machine: "workbox-test");
        var facade = _sessions.Get(remote)!;
        // Complete the method-local source-derived snapshot, without inventing a durable copied-owner fact.
        _database.Writer.Write("test.canonical-mirror", scope => scope.Execute("""
            UPDATE engine_mirror_row SET row_json=json_set(row_json,
              '$.provider',$provider,'$.folder','/recorded/remote','$.updatedMs',$updated)
            WHERE entity_kind='session' AND entity_id=$id AND machine='fixture-engine:workbox-test';
            """, new("$provider", facade.Agent), new("$updated", facade.UpdatedAt), new("$id", remote)));
        OpenEngineNeed(remote, "remote-a");
        var expected = new ExpectedSource("fixture-engine:workbox-test", 1, "workbox-test");
        var initial = Read(locations: ["remote"]);
        Assert.Equal(1, initial.Totals.Needs);
        var canonical = Assert.Single(initial.Sessions, row => row.SessionId == remote && row.Source == expected);
        Assert.Equal("recorded-model", canonical.Model);
        Assert.Equal(expected, canonical.Source);
        var unknown = Assert.Single(initial.Sessions, row => row.SessionId == remote && row.Source is null);
        Assert.Null(unknown.Model);
        Assert.Null(unknown.Worktree);
        _database.Writer.Write("test.replace-source", scope => scope.Execute(
            "UPDATE engine_mirror SET generation=2 WHERE name='workbox-test'"));
        var replaced = Read(locations: ["remote"]);
        Assert.Equal(0, replaced.Totals.Needs);
        Assert.Contains(replaced.Sessions, row => row.SessionId == remote);
        Assert.All(replaced.Sessions.Where(row => row.SessionId == remote), row =>
        {
            Assert.Null(row.Model);
            Assert.Null(row.Source);
        });
        _database.Writer.Write("test.restore-contradiction", scope => scope.Execute("""
            UPDATE engine_mirror SET generation=1 WHERE name='workbox-test';
            UPDATE engine_mirror_row SET row_json=json_set(row_json,'$.machine','different-owner') WHERE entity_kind='session';
            """));
        var contradicted = Read(locations: ["remote"]);
        Assert.Equal(0, contradicted.Totals.Needs);
        Assert.Contains(contradicted.Sessions, row => row.SessionId == remote);
        Assert.All(contradicted.Sessions.Where(row => row.SessionId == remote), row =>
        {
            Assert.Null(row.Model);
            Assert.Null(row.Source);
        });
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
                    new("$json", JsonSerializer.Serialize(new
                    {
                        id = session,
                        machine,
                        holder = "local",
                        state = "idle",
                        parentId = parent,
                        model = "recorded-model"
                    })));
            }
            else scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                    activity_state,parent_id,holder,epoch,cancel_requested)
                VALUES($session,1,$machine,1,1,$provider,'recorded-model','/recorded/folder','Recorded','idle','idle',$parent,'local',0,0);
                """, new("$session", session), new("$machine", _engineMachine), new("$provider", facade.Agent), new("$parent", parent));
            return 0;
        });
    }

    [Fact]
    public void A_step_stopped_for_retry_counts_once_and_a_question_for_a_session_not_at_all()
    {
        var owner = Parent(title: "Owner");
        Assert.Equal(new RailTotals(0), Read().Totals);
        _database.Writer.Write("test.failed-step", scope =>
        {
            scope.Execute("""
                INSERT INTO engine_run(id,version,machine,created_ms,updated_ms,owner_session_id,state,owner_loss_limit_ms,definition_hash)
                VALUES('run',1,$machine,1,1,$owner,'running',1,'hash');
                INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,run_id,name,state,attempt,attempt_in_generation,max_attempts,required,rule_skippable)
                VALUES('step',1,$machine,1,1,'run','step','failed',1,1,2,1,0);
                """, new("$machine", _engineMachine), new("$owner", owner));
            return 0;
        });
        Assert.Equal(new RailTotals(1), Read().Totals);

        var mirrored = Parent(machine: "work", title: "Remote");
        _database.Writer.Write("test.mirror-session", scope => scope.Execute("""
            UPDATE engine_mirror_row AS m SET row_json=json_set(m.row_json,'$.provider',s.agent,'$.folder',s.repo_url,'$.updatedMs',s.updated_at,'$.title',s.title)
            FROM session_info s WHERE m.entity_kind='session' AND m.entity_id=s.session_id AND s.session_id=$id;
            """, new SqlArg("$id", mirrored)));
        OpenEngineNeed(mirrored, "for-owner");
        Assert.Equal(new RailTotals(2), Read().Totals);
        _database.Writer.Write("test.mirror", scope =>
        {
            scope.Execute("""
                INSERT INTO engine_mirror_row(machine,entity_kind,entity_id,generation,version,row_json)
                VALUES('fixture-engine:work','needs_me','not-for-owner',1,1,$need),('fixture-engine:work','step','stopped',1,1,$step);
                """, new("$need", JsonSerializer.Serialize(new { id = "not-for-owner", machine = "fixture-engine:work", sessionId = mirrored, state = "open", forOwner = false })),
                new("$step", JsonSerializer.Serialize(new { id = "stopped", machine = "fixture-engine:work", ownerSessionId = mirrored, needsOwner = true })));
            return 0;
        });
        Assert.Equal(new RailTotals(3), Read().Totals);
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
                    new("$json", JsonSerializer.Serialize(new { id, machine, sessionId = session, state = "open", forOwner = true })));
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
