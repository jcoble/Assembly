using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Needs;

public sealed class ForwardedParentNeedTests
{
    [Fact]
    public void Forwarded_parent_uses_one_actual_need_transition_and_original_private_receipt_after_owner_changes()
    {
        using var f = new EngineFixture();
        f.SeedSession("parent", state: "foreign", holder: "source");
        f.Db.Writer.Write("test.foreign-parent", scope => scope.Execute("UPDATE engine_session SET machine='source' WHERE id='parent'"));
        f.SeedSession("child", state: "running", parentId: "parent", epoch: 7);
        var opened = f.Apply(new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "adapter"), "need", 0, 7, 1000),
            new OpenNeed("child", null, null, "question", "Question", [], null, null, null));
        Assert.True(opened.Applied, opened.Error);
        var target = new NeedAnswerTarget("need", 1, "child", f.Machine, 7, "local", null, "question", false,
            "parent", "source", "parent", null, null);
        var operation = Guid.NewGuid(); var source = new ParentNeedSource(operation, "parent", 3, "source", "need", ParentNeedAction.Answer);
        var local = EngineCommands.PrepareParentNeed(operation, new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("answer")), target, null, default);
        var prepared = EngineCommands.PrepareForwardedParentNeed(source, target, local);
        var commands = new EngineCommands(f.Machine, f.Limits);
        var result = f.Db.Writer.Write("test.forwarded-answer", scope => commands.ApplyForwardedParentNeed(scope, prepared, 1001));
        Assert.True(result.Applied, result.Error); Assert.Equal(7, result.Epoch);
        Assert.Equal("answered", f.State("engine_needs_me", "need"));
        Assert.Equal("parent", f.Text("SELECT answered_by FROM engine_needs_me WHERE id='need'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need-answer/need'"));
        f.Db.Writer.Write("test.owner-changed", scope => scope.Execute("UPDATE engine_session SET epoch=8,holder='other' WHERE id='child'"));
        f.Reopen(); commands = new(f.Machine, f.Limits);
        var replay = f.Db.Writer.Write("test.forwarded-replay", scope => commands.ApplyForwardedParentNeed(scope, prepared, 2000));
        Assert.Equal(result, replay);
        var altered = EngineCommands.PrepareParentNeed(operation, new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("changed")), target, null, default);
        var conflict = EngineCommands.PrepareForwardedParentNeed(source, target, altered);
        Assert.Equal("command_id_conflict", f.Db.Writer.Write("test.forwarded-conflict", scope => commands.ApplyForwardedParentNeed(scope, conflict, 3000)).Error);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need-answer/need'"));
    }
}
