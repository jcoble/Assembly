using Assembly.Application.Common.Database;
using Microsoft.Data.Sqlite;
using Assembly.Engine.Messages;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class SessionLimitTests
{
    [Fact]
    public void Default_depth_refuses_grandchild_with_the_depth_contract()
    {
        using var f = new EngineFixture();
        f.SeedSession("top");
        f.SeedSession("child", parentId: "top");
        var result = f.Apply(new StartSession("codex", "m", "/repo", "Grandchild", "child", null, null),
            "grandchild", actor: new(ActorKind.Session, "child"));
        Assert.Equal("depth_limit", result.Error);
        Assert.Equal(2, f.Count("engine_session"));
        Assert.Empty(f.Events());
    }

    [Fact]
    public void Broken_ancestry_is_refused_without_mutation()
    {
        using var f = new EngineFixture(limits: new(() => 9, () => 5));
        f.SeedSession("parent");
        CorruptParent(f, "parent", "missing");
        var result = f.Apply(new StartSession("codex", "m", "/repo", "Child", "parent", null, null),
            "child", actor: new(ActorKind.Session, "parent"));
        Assert.Equal("bad_args", result.Error);
        Assert.Equal(1, f.Count("engine_session"));
        Assert.Empty(f.Events());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(9, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Grandchild_follows_current_normalized_depth(int depth, bool allowed)
    {
        using var f = new EngineFixture(limits: new(() => depth, () => 5));
        f.SeedSession("top");
        f.SeedSession("child", parentId: "top");
        var result = Start(f, "child", "grandchild");
        Assert.Equal(allowed, result.Applied);
        Assert.Equal(allowed ? null : "depth_limit", result.Error);
        Assert.Equal(allowed ? 3 : 2, f.Count("engine_session"));
    }

    [Fact]
    public void Depth_change_applies_to_fresh_commands_and_preserves_receipt_replay()
    {
        var depth = 1;
        var reads = 0;
        using var f = new EngineFixture(limits: new(() => { reads++; return depth; }, () => 5));
        f.SeedSession("top");
        f.SeedSession("child", parentId: "top");
        var id = Guid.NewGuid();
        var command = new StartSession("codex", "m", "/repo", "Grandchild", "child", null, null);
        var actor = new EngineActor(ActorKind.Session, "child");
        var refused = f.Apply(command, "refused", actor: actor, commandId: id);
        Assert.Equal("depth_limit", refused.Error);
        Assert.Equal(1, reads);
        depth = 9;
        Assert.Equal(refused, f.Apply(command, "refused", actor: actor, commandId: id));
        Assert.Equal(1, reads);
        Assert.True(f.Apply(command, "allowed", actor: actor).Applied);
        Assert.Equal(2, reads);
        depth = 1;
        Assert.True(f.Apply(new SendMessage("child", "top", "Still here", "prompt"), "message", actor: actor).Applied);
        Assert.True(f.Apply(new SetSessionState("running", null, null), "child", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("depth_limit", f.Apply(command, "refused-again", actor: actor).Error);
        Assert.Equal(3, f.Count("engine_session"));
    }

    [Theory]
    [InlineData(8, 9)]
    [InlineData(4096, 4097)]
    public void Deep_chain_uses_the_configured_boundary(int parentDepth, int limit)
    {
        using var f = new EngineFixture(limits: new(() => limit, () => 5));
        SeedChain(f, parentDepth);
        Assert.True(Start(f, $"node-{parentDepth}", "allowed").Applied);
        Assert.Equal("depth_limit", Start(f, "allowed", "too-deep").Error);
        Assert.Equal(parentDepth + 2, f.Count("engine_session"));
    }

    [Fact]
    public void Cyclic_ancestry_terminates_without_changes()
    {
        using var f = new EngineFixture(limits: new(() => int.MaxValue, () => 5));
        f.SeedSession("a");
        f.SeedSession("b", parentId: "a");
        CorruptParent(f, "a", "b");
        Assert.Equal("bad_args", Start(f, "b", "child").Error);
        Assert.Equal(2, f.Count("engine_session"));
        Assert.Empty(f.Events());
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(25, 10)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void Only_unfinished_children_count_against_normalized_limit(int setting, int effective)
    {
        using var f = new EngineFixture(limits: new(() => 1, () => setting));
        f.SeedSession("top");
        f.SeedSession("finished", "ended", "top");
        for (var i = 0; i < effective; i++) Assert.True(Start(f, "top", $"child-{i}").Applied);
        Assert.Equal("child_limit", Start(f, "top", "extra").Error);
    }

    internal static void SeedChain(EngineFixture f, int depth)
    {
        f.Db.Writer.Write("test.chain", scope =>
        {
            for (var i = 0; i <= depth; i++) scope.Execute("""
                INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                    activity_state,parent_id,holder,epoch,cancel_requested)
                VALUES($id,0,$machine,1000,1000,'codex','model','/test','Test','idle','idle',$parent,'local',0,0)
                """, new("$id", $"node-{i}"), new("$machine", f.Machine), new("$parent", i == 0 ? null : $"node-{i - 1}"));
            return 0;
        });
    }

    private static CommandResult Start(EngineFixture f, string parent, string child) =>
        f.Apply(new StartSession("codex", "m", "/repo", "Child", parent, null, null), child,
            actor: new(ActorKind.Session, parent));

    private static void CorruptParent(EngineFixture f, string id, string parent)
    {
        // A separate test-only connection admits damaged historical data that the real writer never creates.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = f.DatabasePath, ForeignKeys = false, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE engine_session SET parent_id=$parent WHERE id=$id";
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
