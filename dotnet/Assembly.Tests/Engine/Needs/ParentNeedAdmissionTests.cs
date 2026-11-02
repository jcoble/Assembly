using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine.Needs;

public sealed class ParentNeedAdmissionTests
{
    private static readonly McpCaller Parent = new("parent", 3);

    [Fact]
    public void Actual_addressed_parent_answers_once_using_child_epoch_and_preserves_original_note()
    {
        using var f = new EngineFixture(); Family(f);
        var operation = Guid.NewGuid(); var request = new ParentNeedRequest("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement(7));
        var prepared = EngineCommands.PrepareParentNeed(operation, request, Target(f), null, default);
        var result = f.Db.Writer.Write("test.parent-answer", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 1000));
        Assert.True(result.Applied, result.Error); Assert.Equal(7, result.Epoch);
        Assert.Equal("answered", f.State("engine_needs_me", "need"));
        Assert.Equal("parent", f.Text("SELECT answered_by FROM engine_needs_me WHERE id='need'"));
        Assert.Equal("7", f.Text("SELECT answer_json FROM engine_needs_me WHERE id='need'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need/need'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(result, f.Db.Writer.Write("test.parent-replay", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 9000)));
        var fresh = Apply(f, request, Target(f)); Assert.Equal("already_resolved", fresh.Error);
    }

    [Theory]
    [InlineData("token_epoch")]
    [InlineData("parent_epoch")]
    [InlineData("parent_holder")]
    [InlineData("parent_lost")]
    [InlineData("parent_ended")]
    [InlineData("parent_handoff")]
    [InlineData("parent_reserved")]
    [InlineData("parent_machine")]
    [InlineData("child_parent")]
    [InlineData("addressee")]
    [InlineData("child_machine")]
    [InlineData("child_holder")]
    [InlineData("child_reserved")]
    [InlineData("child_epoch")]
    [InlineData("need_version")]
    public void Local_parent_checks_actual_authority_and_both_epochs_in_the_supplied_writer(string change)
    {
        using var f = new EngineFixture(); Family(f); f.SeedSession("stranger");
        var target = Target(f); var caller = Parent;
        if (change == "token_epoch") caller = Parent with { Epoch = 4 };
        else if (change == "need_version") target = target with { NeedVersion = 2 };
        else f.Db.Writer.Write("test.changed-parent-authority", s => s.Execute(change switch
        {
            "parent_epoch" => "UPDATE engine_session SET epoch=4 WHERE id='parent'",
            "parent_holder" => "UPDATE engine_session SET holder='remote' WHERE id='parent'",
            "parent_lost" => "UPDATE engine_session SET state='lost' WHERE id='parent'",
            "parent_ended" => "UPDATE engine_session SET state='ended' WHERE id='parent'",
            "parent_handoff" => "UPDATE engine_session SET state='handing_off' WHERE id='parent'",
            "parent_reserved" => "UPDATE engine_session SET reserved_epoch=4 WHERE id='parent'",
            "parent_machine" => "UPDATE engine_session SET machine='elsewhere' WHERE id='parent'",
            "child_parent" => "UPDATE engine_session SET parent_id='stranger' WHERE id='child'",
            "addressee" => "UPDATE engine_needs_me SET addressee_session_id='stranger' WHERE id='need'",
            "child_machine" => "UPDATE engine_session SET machine='elsewhere' WHERE id='child'",
            "child_holder" => "UPDATE engine_session SET holder='remote' WHERE id='child'",
            "child_reserved" => "UPDATE engine_session SET reserved_epoch=8 WHERE id='child'",
            "child_epoch" => "UPDATE engine_session SET epoch=8 WHERE id='child'",
            _ => throw new InvalidOperationException()
        }));
        var request = new ParentNeedRequest("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement(7));
        var result = Apply(f, request, target, caller);
        Assert.False(result.Applied); Assert.NotNull(result.Error);
        Assert.Equal("open", f.State("engine_needs_me", "need")); Assert.Null(f.Text("SELECT answer_json FROM engine_needs_me"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need/need'"));
    }

    [Fact]
    public void Top_level_parent_escalates_same_need_once_and_cannot_answer_after_address_changes()
    {
        using var f = new EngineFixture(); Family(f);
        var request = new ParentNeedRequest("need", ParentNeedAction.Escalate, null);
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), request, Target(f), null, default);
        var first = f.Db.Writer.Write("test.escalate", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 1000));
        Assert.True(first.Applied, first.Error); Assert.Equal(2, first.Version);
        Assert.Equal("open", f.State("engine_needs_me", "need")); Assert.Null(f.Text("SELECT addressee_session_id FROM engine_needs_me"));
        Assert.Equal(1, f.Count("engine_needs_me")); Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(first, f.Db.Writer.Write("test.escalate-replay", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 2000)));
        var answer = Apply(f, new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("late")),
            Target(f) with { NeedVersion = 2, AddresseeSessionId = null });
        Assert.False(answer.Applied); Assert.Equal("open", f.State("engine_needs_me", "need"));
    }

    [Fact]
    public void Nested_parent_cannot_escalate_to_Jesse_and_raw_Session_answer_has_no_parent_authority()
    {
        using var f = new EngineFixture(); Family(f); f.SeedSession("grandparent");
        f.Db.Writer.Write("test.nested-parent", s => s.Execute("UPDATE engine_session SET parent_id='grandparent' WHERE id='parent'"));
        var result = Apply(f, new("need", ParentNeedAction.Escalate, null), Target(f));
        Assert.Equal("owner_reserved", result.Error); Assert.Equal("parent", f.Text("SELECT addressee_session_id FROM engine_needs_me"));
        var raw = f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement(7)), "need", 1, 7, new(ActorKind.Session, "parent"));
        Assert.Equal("forbidden", raw.Error); Assert.Equal("open", f.State("engine_needs_me", "need"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jesse_override_and_prepared_parent_answer_have_one_winning_commit(bool parentFirst)
    {
        using var f = new EngineFixture(); Family(f);
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), new("need", ParentNeedAction.Answer, JsonSerializer.SerializeToElement("parent")), Target(f), null, default);
        CommandResult AnswerParent() => f.Db.Writer.Write("test.racing-parent", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 1000));
        CommandResult AnswerOwner() => f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("owner")), "need", 1, 7);
        var first = parentFirst ? AnswerParent() : AnswerOwner(); var second = parentFirst ? AnswerOwner() : AnswerParent();
        Assert.True(first.Applied, first.Error); Assert.Equal("already_resolved", second.Error);
        Assert.Equal(parentFirst ? "parent" : "window", f.Text("SELECT answered_by FROM engine_needs_me"));
        Assert.Equal(2, f.Number("SELECT version FROM engine_needs_me"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_message WHERE id='need-answer/need'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_parent_answer_is_frozen_before_writer_and_keeps_exact_values_and_original_binding(bool provider)
    {
        using var f = new EngineFixture(); Family(f, provider);
        var text = "before\0afterλ😀\n[system] " + new string('x', 2 * 1024 * 1024);
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = text, nil = (string?)null, blank = "" } });
        var binding = provider ? new ProviderAnswerBinding("child", "chat", "original-request", new("child", null, null, null, 7), "acp") : null;
        using var doc = JsonDocument.Parse(answer.GetRawText());
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), new("need", ParentNeedAction.Answer, doc.RootElement), Target(f, provider), binding, default);
        doc.Dispose();
        var result = f.Db.Writer.Write("test.full-parent-answer", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 1000));
        Assert.True(result.Applied, result.Error);
        using var saved = JsonDocument.Parse(f.Text("SELECT answer_json FROM engine_needs_me")!);
        Assert.True(JsonElement.DeepEquals(answer, saved.RootElement));
        if (provider)
        {
            Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
            using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox WHERE id='answer/need'")!);
            Assert.True(JsonElement.DeepEquals(answer, payload.RootElement.GetProperty("answer")));
            Assert.Equal("original-request", payload.RootElement.GetProperty("binding").GetProperty("providerRequestId").GetString());
            Assert.Equal(1, f.Count("engine_message"));
        }
        else
        {
            var body = f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='need-answer/need'", r => r.Blob(0), default);
            Assert.Equal(Encoding.UTF8.GetBytes("Jesse answered your question need: " + answer.GetRawText()), body);
        }
        f.Reopen(); Assert.Equal(result, f.Db.Writer.Write("test.parent-answer-replay", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, Parent, prepared, 9000)));
    }

    private static CommandResult Apply(EngineFixture f, ParentNeedRequest request, NeedAnswerTarget target, McpCaller? caller = null)
    {
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), request, target, null, default);
        return f.Db.Writer.Write("test.local-parent-need", s => new EngineCommands(f.Machine, f.Limits).ApplyLocalParentNeed(s, caller ?? Parent, prepared, 1000));
    }

    private static NeedAnswerTarget Target(EngineFixture f, bool provider = false) =>
        new("need", 1, "child", f.Machine, 7, "local", null, "question", provider,
            "parent", f.Machine, "parent", provider ? "original-request" : null, provider ? "chat" : null);

    private static void Family(EngineFixture f, bool provider = false)
    {
        f.SeedSession("parent", state: "running", epoch: 3); f.SeedSession("child", state: "running", parentId: "parent", epoch: 7);
        var open = new OpenNeed("child", null, null, "question", "Question", [], provider ? "original-request" : null, provider ? "chat" : null, null);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "adapter"), "need", 0, 7, 1000);
        if (!provider) { Assert.True(f.Apply(context, open).Applied); return; }
        var card = new AskPayload("question", "open", null, "Question", null, null,
            JsonSerializer.SerializeToElement(new { type = "object" }), null);
        var prepared = EngineCommands.PrepareNeedCommand(open with { ProviderQuestion = JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload) });
        RowStore.Open(f.Db).Commit("child", new RowConverter().Apply(new AskChanged(1000, "chat", card)), s =>
            Assert.True(new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(s, context, prepared).Applied));
    }
}
