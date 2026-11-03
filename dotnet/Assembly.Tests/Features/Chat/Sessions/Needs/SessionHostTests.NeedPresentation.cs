using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Actual_form_required_types_and_choices_share_direct_and_prepared_answer_validation()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId); host.Send("form");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(18, "elicitation/create", JsonSerializer.Serialize(new
        {
            sessionId = FakeLink.AcpSessionId,
            mode = "form",
            message = "Actual supported form",
            requestedSchema = new
            {
                type = "object",
                required = new[] { "name", "choice", "many" },
                properties = new
                {
                    name = new { type = "string" },
                    choice = new { type = "string", oneOf = new[] { new { @const = "one" }, new { @const = "two" } } },
                    many = new { type = "array", items = new { anyOf = new[] { new { @const = "a" }, new { @const = "b" } } } }
                }
            }
        }));
        var card = (await RowsUntil(row => row.Kind == "ask"))[^1]; var id = card.ItemId![4..];
        foreach (var json in new[]
        {
            """{"action":"accept"}""",
            """{"action":"accept","content":{"choice":"one","many":["a"]}}""",
            """{"action":"accept","content":{"name":7,"choice":"one","many":["a"]}}""",
            """{"action":"accept","content":{"name":"Name","choice":"other","many":["a"]}}""",
            """{"action":"accept","content":{"name":"Name","choice":"one","many":["other"]}}"""
        })
        {
            using var invalid = JsonDocument.Parse(json);
            Assert.Null(host.PrepareProviderAnswer(id, invalid.RootElement));
            Assert.False(host.Answer(id, invalid.RootElement));
        }
        Assert.Equal("open", _database.QueryFirst("SELECT state FROM engine_needs_me WHERE id=$id", row => row.Text(0), default, new SqlArg("$id", id)));
        Assert.Equal(1, _database.QueryFirst("SELECT version FROM engine_needs_me WHERE id=$id", row => row.Long(0), default, new SqlArg("$id", id)));
        var name = "Exact\0λ😀" + new string('x', 80000);
        var answer = JsonSerializer.SerializeToElement(new
        {
            action = "accept",
            content = new
            { name, choice = "two", many = new[] { "a", "b" }, extra = new object[] { 1, true, new { value = "retained" } } }
        });
        var prepared = host.PrepareProviderAnswer(id, answer);
        Assert.NotNull(prepared);
        Assert.True(host.AnswerProviderRequest(prepared.Binding, prepared.Answer));
        var actual = await agent.ResultAsync();
        Assert.Equal(name, actual.GetProperty("content").GetProperty("name").GetString());
        Assert.True(JsonElement.DeepEquals(answer.GetProperty("content"), actual.GetProperty("content")));
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Form_without_required_fields_keeps_optional_accept_content()
    {
        _host.Send("legacy form");
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.RequestAsync(19, "elicitation/create", JsonSerializer.Serialize(new
        { sessionId = FakeLink.AcpSessionId, mode = "form", message = "Optional form", requestedSchema = new { type = "object" } }));
        var card = (await RowsUntil(row => row.Kind == "ask"))[^1]; var id = card.ItemId![4..];
        Assert.True(_host.Answer(id, JsonSerializer.SerializeToElement(new { action = "accept" })));
        var actual = await agent.ResultAsync();
        Assert.Equal("accept", actual.GetProperty("action").GetString());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
    }
}
