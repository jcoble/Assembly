using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Reading;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine;

public sealed class NeedPresentationTests
{
    [Fact]
    public void Actual_permission_labels_order_title_and_stable_number_survive_reopen()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(OpenProvider(f, "n", Permission()).Applied);
        var item = Display(f, "n");
        Assert.Equal("Inspect command", item.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("recommendation").ValueKind);
        Assert.Equal("permission", item.GetProperty("inputMode").GetString());
        Assert.False(item.GetProperty("freeText").GetBoolean());
        Assert.Equal("once", item.GetProperty("options")[0].GetProperty("id").GetString());
        Assert.Equal("Allow this time", item.GetProperty("options")[0].GetProperty("label").GetString());
        Assert.Equal("deny", item.GetProperty("options")[1].GetProperty("id").GetString());
        Assert.Equal(1, item.GetProperty("displayNumber").GetInt64());
        Assert.Equal("[\"once\",\"deny\"]", item.GetProperty("optionsJson").GetString());
        f.Reopen();
        Assert.Equal(1, Display(f, "n").GetProperty("displayNumber").GetInt64());
        Assert.Equal(2, f.Number("SELECT next_need_number FROM engine_meta"));
    }

    [Fact]
    public void Failed_need_insert_rolls_back_number_chat_row_and_events()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(OpenProvider(f, "first", Permission()).Applied);
        var events = f.Count("engine_event"); var rows = f.Count("rows");
        f.Db.Writer.Write("test.fail-need", scope => scope.Execute("CREATE TRIGGER reject_need BEFORE INSERT ON engine_needs_me BEGIN SELECT RAISE(ABORT,'injected'); END;"));
        Assert.Throws<SqliteException>(() => OpenProvider(f, "second", Permission()));
        Assert.Equal(1, f.Count("engine_needs_me")); Assert.Equal(events, f.Count("engine_event"));
        Assert.Equal(rows, f.Count("rows")); Assert.Equal(2, f.Number("SELECT next_need_number FROM engine_meta"));
    }

    [Fact]
    public void Historical_numbers_backfill_once_by_created_time_and_id_without_fabricating_answer_origin()
    {
        using var f = new EngineFixture(predecessor: scope =>
        {
            // Real captured v10 DDL plus its actual workflow upgrade produces the v11 predecessor.
            var captured = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Workflows", "Fixtures", "schema10", "actual-v10.sql"));
            scope.Execute(captured[..captured.IndexOf("INSERT INTO", StringComparison.Ordinal)]);
            Assembly.Engine.Workflows.Persistence.WorkflowSchema.Upgrade(scope);
            scope.Execute("INSERT INTO engine_meta(singleton,schema_version,machine_id,next_seq,retained_after,snapshot_seq) VALUES(1,11,'need-schema',1,0,0)");
        });
        f.SeedSession();
        f.Db.Writer.Write("test.historical-needs", scope => scope.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,answer_json,answered_by,answered_ms)
            VALUES('b',1,'need-schema',1000,1000,'s','question','Recorded question','[]','open',NULL,NULL,NULL),
                  ('a',2,'need-schema',1000,1000,'s','question','Recorded question','[]','answered','"historical answer"','owner',1000);
            """));
        f.Reopen();
        Assert.Equal(1, Display(f, "a").GetProperty("displayNumber").GetInt64());
        Assert.Equal(2, Display(f, "b").GetProperty("displayNumber").GetInt64());
        Assert.Equal(JsonValueKind.Null, Display(f, "a").GetProperty("answeredOrigin").ValueKind);
        Assert.Equal("historical answer", Display(f, "a").GetProperty("answer").GetString());
        f.Reopen();
        Assert.Equal(1, Display(f, "a").GetProperty("displayNumber").GetInt64());
        Assert.Equal(3, f.Number("SELECT next_need_number FROM engine_meta"));
    }

    [Fact]
    public void Invalid_typed_mcp_choice_is_refused_without_provenance_or_answer_effect()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var command = new OpenNeed("s", null, null, "choice", "Choose", ["once", "deny"], null, null, null,
            Presentation: new(null, null, "choice", null, [new("once", "Allow", "choice"), new("deny", "Decline", "choice")], false));
        Assert.True(f.Apply(command, "n", actor: new(ActorKind.Session, "s")).Applied);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, "n", 1, 0,
            new("answered", JsonSerializer.SerializeToElement("not-offered")), null, null, default, new("phone", "device-1", null));
        var result = f.Db.Writer.Write("test.invalid-choice", scope => new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(scope, prepared, 1001));
        Assert.Equal("bad_args", result.Error);
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Null(f.Text("SELECT answered_origin_json FROM engine_needs_me"));
    }

    [Fact]
    public void First_structured_answer_records_trusted_origin_once_with_existing_provider_binding()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(OpenProvider(f, "n", Permission()).Applied);
        var answer = JsonSerializer.SerializeToElement(new { optionId = "once" });
        var commandId = Guid.NewGuid();
        var first = Answer(f, "n", answer, new("phone", "device-1", null), commandId);
        Assert.True(first.Applied);
        Assert.Equal(first, Answer(f, "n", answer, new("desktop", null, "board"), commandId));
        Assert.Equal("already_resolved", Answer(f, "n", answer, new("desktop", null, "board")).Error);
        var item = Display(f, "n");
        Assert.Equal("owner", item.GetProperty("answeredBy").GetString());
        Assert.Equal("once", item.GetProperty("answer").GetProperty("optionId").GetString());
        Assert.Equal("phone", item.GetProperty("answeredOrigin").GetProperty("channel").GetString());
        Assert.Equal("device-1", item.GetProperty("answeredOrigin").GetProperty("deviceId").GetString());
        Assert.Equal(1, f.Count("engine_outbox"));
        f.Reopen();
        Assert.Equal("device-1", Display(f, "n").GetProperty("answeredOrigin").GetProperty("deviceId").GetString());
    }

    [Fact]
    public void Form_keeps_exact_schema_and_canonical_action_content_answer()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = new[] { "choice" },
            properties = new
            {
                choice = new { type = "string", @enum = new[] { "one", "two" } },
                details = new { type = "string" }
            }
        });
        var card = new AskPayload("question", "open", "Choose", "Full form", null, null, schema, null);
        Assert.True(OpenProvider(f, "form", card).Applied);
        Assert.True(JsonElement.DeepEquals(schema, Display(f, "form").GetProperty("schema")));
        var valid = JsonSerializer.SerializeToElement(new { action = "accept", content = new { choice = "two", details = "Keep this exact string" } });
        Assert.True(Answer(f, "form", valid, new("desktop", null, "rail")).Applied);
        Assert.True(JsonElement.DeepEquals(valid, Display(f, "form").GetProperty("answer")));
    }

    [Fact]
    public void Structured_recommendation_is_not_inferred_and_invalid_metadata_has_no_effects()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var command = new OpenNeed("s", null, null, "question", "First line\nRecommendation is just prose", [], null, null, null,
            Presentation: new(null, "Actual structured recommendation", "text", null, [], true));
        Assert.True(f.Apply(command, "mcp", actor: new(ActorKind.Session, "s")).Applied);
        Assert.Equal("First line", Display(f, "mcp").GetProperty("title").GetString());
        Assert.Equal("Actual structured recommendation", Display(f, "mcp").GetProperty("recommendation").GetString());
        var before = f.Count("engine_event");
        Assert.Equal("bad_args", f.Apply(command with { Presentation = command.Presentation! with { Recommendation = new string('x', 301) } },
            "bad-metadata", actor: new(ActorKind.Session, "s")).Error);
        Assert.Equal(1, f.Count("engine_needs_me")); Assert.Equal(before, f.Count("engine_event"));
        Assert.Equal(2, f.Number("SELECT next_need_number FROM engine_meta"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_presentation_budget_counts_encoded_actual_addressee_owner_before_effects(bool escapedOwner)
    {
        using var f = new EngineFixture();
        var owner = "owner-" + new string(escapedOwner ? 'λ' : 'x', 3000);
        var registered = f.Apply(new global::Assembly.Engine.Sessions.RegisterForeignSession(owner, "codex", "Recorded parent"),
            "parent", actor: new(ActorKind.System, "machine:" + owner));
        Assert.True(registered.Applied, registered.Error);
        f.SeedSession("child", parentId: "parent");
        var text = new string('q', 16000);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(owner) + System.Text.Encoding.UTF8.GetByteCount(text) < 32 * 1024);
        var ownerJson = JsonSerializer.SerializeToUtf8Bytes(owner, EngineJson.Default.String);
        if (escapedOwner) Assert.True(ownerJson.Length > System.Text.Encoding.UTF8.GetByteCount(owner) + 2);
        var events = f.Count("engine_event");
        var effects = f.Count("engine_outbox");
        var messages = f.Count("engine_message");
        var number = f.Number("SELECT next_need_number FROM engine_meta");
        var version = f.Number("SELECT version FROM engine_session WHERE id='child'");
        var command = new OpenNeed("child", null, null, "question", text, [], null, null, null,
            Presentation: new("Question", null, "text", null, [], true));
        var result = f.Apply(command, "budget-need", actor: new(ActorKind.System, "adapter"));
        if (escapedOwner)
        {
            Assert.False(result.Applied);
            Assert.Equal("bad_args", result.Error);
            Assert.Equal(0, f.Count("engine_needs_me"));
            Assert.Equal(events, f.Count("engine_event"));
            Assert.Equal(effects, f.Count("engine_outbox"));
            Assert.Equal(messages, f.Count("engine_message"));
            Assert.Equal(number, f.Number("SELECT next_need_number FROM engine_meta"));
            Assert.Equal(version, f.Number("SELECT version FROM engine_session WHERE id='child'"));
            Assert.Equal("idle", f.State("engine_session", "child"));
        }
        else
        {
            Assert.True(result.Applied, result.Error);
            var display = Display(f, "budget-need");
            Assert.Equal(owner, display.GetProperty("addresseeMachine").GetString());
            Assert.Equal(text, display.GetProperty("text").GetString());
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(display, EngineJson.Default.JsonElement).Length <= 32 * 1024);
        }
    }

    private static AskPayload Permission() => new("permission", "open", "Inspect command", "Run the recorded command?", null,
        [new("once", "Allow this time", "allow_once"), new("deny", "Decline", "reject_once")], null, null);

    [Fact]
    public void Full_provider_card_is_retained_when_its_presentation_cannot_fit_display()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var label = "Recorded label\0λ😀" + new string('x', 70000);
        var card = Permission() with { Message = new string('q', 90000), Options = [new("once", label, "allow_once")] };
        Assert.True(OpenProvider(f, "large-card", card).Applied);
        using var stored = JsonDocument.Parse(f.Text("SELECT presentation_json FROM engine_needs_me")!);
        Assert.Equal(label, stored.RootElement.GetProperty("options")[0].GetProperty("label").GetString());
        Assert.Equal(card.Message, f.Text("SELECT text FROM engine_needs_me"));
        var display = Display(f, "large-card");
        Assert.Equal(JsonValueKind.Null, display.GetProperty("options").ValueKind);
        Assert.False(display.GetProperty("textComplete").GetBoolean());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(display.GetRawText()) <= 32 * 1024);
    }

    [Fact]
    public void Full_canonical_answer_and_effect_survive_while_large_display_answer_is_explicitly_incomplete()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { body = new { type = "string" } } });
        Assert.True(OpenProvider(f, "large-answer", new("question", "open", "Recorded form", "Fill the form", null, null, schema, null)).Applied);
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = "\0λ😀" + new string('x', 2 * 1024 * 1024) } });
        Assert.True(Answer(f, "large-answer", answer, new("desktop", null, "board")).Applied);
        using var stored = JsonDocument.Parse(f.Text("SELECT answer_json FROM engine_needs_me")!);
        Assert.True(JsonElement.DeepEquals(answer, stored.RootElement));
        using var effect = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox WHERE kind='answer'")!);
        Assert.True(JsonElement.DeepEquals(answer, effect.RootElement.GetProperty("answer")));
        var display = Display(f, "large-answer");
        Assert.Equal(JsonValueKind.Null, display.GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, display.GetProperty("answerText").ValueKind);
        Assert.False(display.GetProperty("answerComplete").GetBoolean());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(answer.GetRawText()), display.GetProperty("answerUtf8Bytes").GetInt64());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(display.GetRawText()) <= 32 * 1024);
    }

    private static CommandResult OpenProvider(EngineFixture f, string id, AskPayload card)
    {
        var command = EngineCommands.PrepareNeedCommand(new OpenNeed("s", null, null, card.Kind,
            card.Message ?? card.Title ?? "", card.Options?.Select(option => option.OptionId).ToArray() ?? [],
            "request/" + id, id, null, JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload)));
        CommandResult result = null!;
        RowStore.Open(f.Db).Commit("s", new RowConverter().Apply(new AskChanged(1000, id, card)), scope =>
            result = new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(scope,
                new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "adapter"), id, 0, 0, 1000, new("adapter", null, null)), command));
        return result;
    }

    private static CommandResult Answer(EngineFixture f, string id, JsonElement answer, CommandOrigin origin, Guid? commandId = null)
    {
        var captured = new PreparedProviderAnswer(new("s", id, "request/" + id, new("s", null, null, null, 0), "fixture-acp"), answer);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(commandId ?? Guid.NewGuid(), f.Machine, id, 1, 0,
            new("answered", answer), captured, null, default, origin);
        return f.Db.Writer.Write("test.answer", scope => new EngineCommands(f.Machine, f.Limits).ApplyPreparedOwnerNeed(scope, prepared, 1001));
    }

    private static JsonElement Display(EngineFixture f, string id) =>
        new EngineReader(f.Db, f.Machine).Entity("needs_me", id, default)!.Value;
}
