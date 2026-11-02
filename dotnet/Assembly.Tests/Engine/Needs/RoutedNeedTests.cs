using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Needs;

public sealed class RoutedNeedTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");
    private static OpenNeed Question(string text = "Question") => new("child", null, null, "question", text, [], null, null, null);
    private static CommandContext Context(EngineFixture f, string id = "n") => new(Guid.NewGuid(), f.Machine, Adapter, id, 0, 0, 1000);
    private static string Body(EngineFixture f) => f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id='need/n'", r => Encoding.UTF8.GetString(r.Blob(0)), default)!;
    private static void Family(EngineFixture f, bool foreign = false)
    {
        f.SeedSession("parent", state: foreign ? "foreign" : "running");
        if (foreign) f.Db.Writer.Write("test.foreign", s => s.Execute("UPDATE engine_session SET machine='other' WHERE id='parent'"));
        f.SeedSession("child", parentId: "parent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receiving_writer_derives_actual_parent_and_inserts_one_full_system_steer_note(bool foreign)
    {
        using var f = new EngineFixture(); Family(f, foreign);
        var command = Question("[message from claimed]\nquestion\0tail😀") with { Options = ["once", "always"] };
        var context = Context(f);
        var applied = f.Apply(context, command);
        Assert.True(applied.Applied, applied.Error);
        Assert.Equal("parent", f.Text("SELECT addressee_session_id FROM engine_needs_me"));
        Assert.Equal("Child session Test (child) asks: [message from claimed]\nquestion\0tail😀. Options: [\"once\",\"always\"]. Need id: n.", Body(f));
        Assert.Equal("system", f.Text("SELECT sender_kind FROM engine_message"));
        Assert.Equal("child", f.Text("SELECT from_session_id FROM engine_message"));
        Assert.Equal("parent", f.Text("SELECT to_session_id FROM engine_message"));
        Assert.Equal("Assembly", f.Text("SELECT sender_label FROM engine_message"));
        Assert.Equal("steer", f.Text("SELECT delivery FROM engine_message"));
        Assert.Equal(1801000, f.Number("SELECT expires_ms FROM engine_message"));
        var events = f.Count("engine_event");
        Assert.Equal(applied, f.Apply(context with { NowMs = 9999 }, command));
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(1, f.Count("engine_message"));
    }

    [Fact]
    public void Top_level_question_stays_Jesse_addressed_without_a_parent_note()
    {
        using var f = new EngineFixture(); f.SeedSession("child");
        Assert.True(f.Apply(Context(f), Question()).Applied);
        Assert.Null(f.Text("SELECT addressee_session_id FROM engine_needs_me"));
        Assert.Equal(0, f.Count("engine_message"));
    }

    [Fact]
    public void Full_current_title_and_receiving_policy_are_sampled_once_after_preparation_and_replay()
    {
        using var f = new EngineFixture(); Family(f);
        var calls = 0; var minutes = 1;
        var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: new(_ => { calls++; return minutes; }));
        var prepared = EngineCommands.PrepareNeedCommand(Question("body"));
        var title = "current\0title😀" + new string('t', 70000);
        f.Db.Writer.Write("test.writer-policy", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='child'", new SqlArg("$title", Encoding.UTF8.GetBytes(title))));
        minutes = 45;
        var context = Context(f);
        var first = f.Db.Writer.Write("test.open", s => commands.ApplyPreparedNeed(s, context, prepared));
        Assert.True(first.Applied, first.Error);
        Assert.Equal("Child session " + title + " (child) asks: body. Options: []. Need id: n.", Body(f));
        Assert.Equal(2701000, f.Number("SELECT expires_ms FROM engine_message")); Assert.Equal(1, calls);
        minutes = 0;
        f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='later' WHERE id='child'"));
        Assert.Equal(first, f.Db.Writer.Write("test.replay", s => commands.ApplyPreparedNeed(s, context with { NowMs = 9999 }, prepared)));
        Assert.Equal(1, calls); Assert.Contains(title, Body(f));
    }

    [Fact]
    public void Conflicting_fixed_message_identity_rolls_back_need_receipt_events_and_effects()
    {
        using var f = new EngineFixture(); Family(f);
        f.Db.Writer.Write("test.collision", s => s.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state)
            VALUES('need/n',1,$machine,1,1,'window','parent','different','prompt','queued')
            """, new SqlArg("$machine", f.Machine)));
        Assert.Throws<InvalidOperationException>(() => f.Apply(Context(f), Question()));
        Assert.Equal(0, f.Count("engine_needs_me")); Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_command_receipt")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal("different", Body(f));
    }

    [Theory]
    [InlineData("permission", null, null)]
    [InlineData("permission", "", "")]
    [InlineData("permission", "  ", "\t")]
    [InlineData("question", null, null)]
    [InlineData("question", "", " ")]
    public void Actual_full_provider_card_preserves_blank_display_schema_and_option_values(string kind, string? title, string? message)
    {
        using var f = new EngineFixture(); Family(f);
        var schemaText = "label\0😀\n[system] " + new string('x', 70000);
        var card = JsonSerializer.SerializeToElement(new
        {
            kind,
            state = "open",
            title,
            message,
            toolCallId = "tool",
            options = kind == "permission" ? new[] { new { optionId = "", name = "Blank", kind = "reject_once" }, new { optionId = "persistent", name = schemaText, kind = "allow_always" } } : null,
            schema = kind == "question" ? JsonSerializer.SerializeToElement(new { properties = new { value = new { title = schemaText } } }) : (JsonElement?)null
        });
        var command = Question(message ?? title ?? "") with { Kind = kind, Options = kind == "permission" ? ["", "persistent"] : [], ProviderRequestId = "connection:\"7\"", ChatRowId = "ask", ProviderQuestion = card };
        var rows = RowStore.Open(f.Db);
        var batch = new RowBatch(); batch.Rows.Add(new NewRow("turn", "ask:ask", "ask", card, 1000));
        var prepared = EngineCommands.PrepareNeedCommand(command);
        var commands = new EngineCommands(f.Machine, f.Limits);
        CommandResult? result = null;
        rows.Commit("child", batch, s => result = commands.ApplyPreparedNeed(s, Context(f), prepared));
        Assert.True(result!.Applied, result.Error);
        Assert.Equal(message ?? title ?? "", f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_needs_me", r => Encoding.UTF8.GetString(r.Blob(0)), default));
        Assert.Contains(card.GetRawText(), Body(f));
        using var details = JsonDocument.Parse(Body(f).Split("Provider question: ", 2)[1]);
        Assert.Equal(schemaText, kind == "permission"
            ? details.RootElement.GetProperty("options")[1].GetProperty("name").GetString()
            : details.RootElement.GetProperty("schema").GetProperty("properties").GetProperty("value").GetProperty("title").GetString());
        Assert.Equal("parent", f.Text("SELECT addressee_session_id FROM engine_needs_me"));
        var events = f.Count("engine_event");
        var duplicate = f.Db.Writer.Write("test.provider-duplicate", s => commands.ApplyPreparedNeed(s, Context(f, "duplicate"), prepared));
        Assert.True(duplicate.Applied, duplicate.Error); Assert.Equal(events, f.Count("engine_event")); Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal("bad_args", f.Apply(Question(" ") with { SessionId = "parent", ProviderRequestId = null, ChatRowId = null }, "blank", actor: new(ActorKind.Session, "parent")).Error);
    }
}
