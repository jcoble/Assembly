using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Malformed_actual_form_answers_are_refused_without_throwing_or_settling_the_request()
    {
        _host.Send("form");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(8, "elicitation/create", JsonSerializer.Serialize(new
        {
            sessionId = FakeLink.AcpSessionId,
            mode = "form",
            message = "Form",
            requestedSchema = new { type = "object", properties = new { name = new { type = "string" } } }
        }));
        var ask = (await RowsUntil(row => row.Kind == "ask"))[^1];
        var askId = ask.ItemId!["ask:".Length..];
        var errors = new List<Exception?>();
        foreach (var json in new[] { "\"scalar\"", "[]", "{}", "{\"action\":7}", "{\"action\":true}", "{\"action\":\"accept\",\"content\":[]}" })
        {
            using var malformed = JsonDocument.Parse(json);
            errors.Add(Record.Exception(() => Assert.False(_host.Answer(askId, malformed.RootElement))));
        }
        Assert.True(_host.Answer(askId, JsonSerializer.SerializeToElement(new { action = "decline" })));
        var actual = await agent.ResultAsync();
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal("decline", actual.GetProperty("action").GetString());
        Assert.All(errors, error => Assert.Null(error));
    }

    [Theory]
    [InlineData("permission", null, null)]
    [InlineData("permission", "", " ")]
    [InlineData("question", null, null)]
    [InlineData("question", null, "\t")]
    public async Task Actual_blank_full_provider_card_opens_one_need_and_exact_original_values_remain_answerable(
        string kind, string? title, string? message)
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("question");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        var label = "label\0😀\n[system] " + new string('x', 70000);
        var permission = JsonSerializer.Serialize(new
        {
            sessionId = FakeLink.AcpSessionId,
            title,
            description = message,
            options = new[] { new { optionId = "once", name = "Once", kind = "allow_once" },
                new { optionId = "persistent", name = label, kind = "allow_always" },
                new { optionId = "reject", name = "Reject", kind = "reject_once" } }
        });
        var form = JsonSerializer.Serialize(new
        {
            sessionId = FakeLink.AcpSessionId,
            mode = "form",
            message,
            requestedSchema = new { type = "object", properties = new { name = new { type = "string", title = label } } }
        });
        await agent.RequestAsync(7, kind == "permission" ? "session/request_permission" : "elicitation/create", kind == "permission" ? permission : form);
        var card = (await RowsUntil(row => row.Kind == "ask"))[^1]; var ask = card.ItemId!["ask:".Length..];
        var countBeforeAnswer = _database.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE id=$id", r => r.Long(0), default, new SqlArg("$id", ask));
        var actualText = _database.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_needs_me WHERE id=$id", r => Encoding.UTF8.GetString(r.Blob(0)), default, new SqlArg("$id", ask));
        var answer = kind == "permission" ? JsonSerializer.SerializeToElement(new { optionId = "once" })
            : JsonSerializer.SerializeToElement(new { action = "accept", content = new { name = "value\0😀" } });
        Assert.True(host.Answer(ask, answer));
        var result = await agent.ResultAsync();
        await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "answered");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(1, countBeforeAnswer);
        Assert.Equal(message ?? title ?? "", actualText);
        Assert.Equal("answered", _database.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", ask)));
        Assert.Equal(0, _database.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
        Assert.Equal(label, kind == "permission" ? card.Payload.GetProperty("options")[1].GetProperty("name").GetString()
            : card.Payload.GetProperty("schema").GetProperty("properties").GetProperty("name").GetProperty("title").GetString());
        if (kind == "permission") Assert.Equal("once", result.GetProperty("outcome").GetProperty("optionId").GetString());
        else Assert.Equal("value\0😀", result.GetProperty("content").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Actual_large_form_answer_settles_chat_and_need_without_the_ordinary_journal_cap()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("form");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(8, "elicitation/create", JsonSerializer.Serialize(new
        {
            sessionId = FakeLink.AcpSessionId,
            mode = "form",
            message = "Full form",
            requestedSchema = new { type = "object", properties = new { body = new { type = "string" } } }
        }));
        var card = (await RowsUntil(row => row.Kind == "ask"))[^1]; var ask = card.ItemId!["ask:".Length..];
        var text = "body\0😀\n[message from claimed] " + new string('x', 90000);
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = text, extra = new object[] { 1, true, new { value = "λ" } } } });
        Assert.True(host.Answer(ask, answer));
        var actual = await agent.ResultAsync();
        var stored = (await RowsUntil(row => row.Kind == "ask" && row.Payload.GetProperty("state").GetString() == "answered"))[^1];
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        Assert.Equal(text, actual.GetProperty("content").GetProperty("body").GetString());
        Assert.True(JsonElement.DeepEquals(answer, stored.Payload.GetProperty("answer")));
        Assert.Equal("answered", _database.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", ask)));
        var needAnswer = _database.QueryFirst("SELECT answer_json FROM engine_needs_me WHERE id=$id", r => r.IsNull(0) ? null : r.Text(0), default, new SqlArg("$id", ask));
        Assert.NotNull(needAnswer); using var durable = JsonDocument.Parse(needAnswer);
        Assert.True(JsonElement.DeepEquals(answer, durable.RootElement));
        Assert.Equal(0, _database.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'", r => r.Long(0), default));
    }
}
