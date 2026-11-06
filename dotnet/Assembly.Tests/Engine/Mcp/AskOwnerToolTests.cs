using System.Security.Claims;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Contracts;
using Assembly.Engine;
using Microsoft.Data.Sqlite;
using Assembly.Infrastructure.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed class AskOwnerToolTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");
    private static OpenNeed Question(string? request = null) => new("s", null, null, "question", "Proceed?", [], request, null, 2000);

    [Fact]
    public async Task Explicit_presentation_metadata_is_retained_and_over_limit_metadata_opens_nothing()
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        var tool = new AskOwnerTool(h.Gate, h.Needs);
        var response = await h.Call(tool, "s", """{"question":"First line is not a recommendation","title":"Actual title","recommendation":"Actual recommendation"}""");
        Assert.False(response.IsError);
        using var presentation = JsonDocument.Parse(h.Fixture.Text("SELECT presentation_json FROM engine_needs_me")!);
        Assert.Equal("Actual title", presentation.RootElement.GetProperty("title").GetString());
        Assert.Equal("Actual recommendation", presentation.RootElement.GetProperty("recommendation").GetString());
        Assert.Equal("text", presentation.RootElement.GetProperty("inputMode").GetString());
        Assert.True(presentation.RootElement.GetProperty("freeText").GetBoolean());
        var invalid = JsonSerializer.Serialize(new { question = "Other", recommendation = new string('x', 301) });
        Assert.True((await h.Call(tool, "s", invalid)).IsError);
        Assert.Equal(1, h.Fixture.Count("engine_needs_me"));
    }

    [Fact]
    public async Task Top_level_question_opens_one_need_and_owned_run()
    {
        await using var h = new Harness();
        h.Fixture.SeedSession("s"); h.Fixture.SeedRun(); h.Fixture.SeedStep();
        var response = await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", """{"question":"Proceed?","runId":"r"}""");
        Assert.False(response.IsError);
        using var json = JsonDocument.Parse(Text(response));
        var id = json.RootElement.GetProperty("needId").GetString()!;
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("open", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("open", h.Fixture.State("engine_needs_me", id));
        Assert.Equal("r", h.Fixture.Text("SELECT run_id FROM engine_needs_me"));
        Assert.Equal("question", h.Fixture.Text("SELECT kind FROM engine_needs_me"));
        Assert.Null(h.Fixture.Text("SELECT step_id FROM engine_needs_me"));
        Assert.Null(h.Fixture.Text("SELECT provider_request_id FROM engine_needs_me"));
        Assert.Equal("needs_me", h.Fixture.State("engine_run", "r"));
        Assert.Equal(1, h.Fixture.Count("engine_needs_me"));
    }

    [Fact]
    public async Task Child_ask_owner_is_refused()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("parent"); h.Fixture.SeedSession("s", parentId: "parent");
        var result = await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", """{"question":"Proceed?"}""");
        Assert.True(result.IsError);
        Assert.Equal("Only a top-level session can ask Jesse. Send your question to your starter with send_message.", Text(result));
        Assert.Equal(0, h.Fixture.Count("engine_needs_me")); Assert.Empty(h.Fixture.Events());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"one\"]")]
    [InlineData("[\"same\",\"same\"]")]
    [InlineData("[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\",\"9\"]")]
    public async Task Choice_needs_two_to_eight_options(string options)
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        var result = await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", "{\"question\":\"Pick\",\"kind\":\"choice\",\"options\":" + options + "}");
        Assert.True(result.IsError); Assert.Equal("ask_owner: options needs 2 to 8 distinct entries.", Text(result));
        Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"question\":null}")]
    [InlineData("{\"question\":\" \"}")]
    [InlineData("{\"question\":\"Q\",\"kind\":\"permission\"}")]
    [InlineData("{\"question\":\"Q\",\"sessionId\":\"other\"}")]
    [InlineData("{\"question\":\"Q\",\"childNeedId\":\"n\"}")]
    [InlineData("{\"question\":\"Q\",\"options\":[1,2]}")]
    public async Task Invalid_arguments_mutate_nothing(string args)
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        Assert.True((await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", args)).IsError);
        Assert.Equal(0, h.Fixture.Count("engine_needs_me")); Assert.Empty(h.Fixture.Events());
    }

    [Fact]
    public async Task Foreign_run_is_refused_through_engine_gate()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("other"); h.Fixture.SeedRun(ownerSessionId: "other"); h.Fixture.SeedSession(runId: "r");
        var result = await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", """{"question":"Proceed?","runId":"r"}""");
        Assert.True(result.IsError); Assert.Equal("ask_owner: Assembly refused these arguments.", Text(result));
        Assert.Equal(0, h.Fixture.Count("engine_needs_me")); Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
    }

    [Fact]
    public void Session_permission_and_step_are_refused_but_system_provider_is_retained()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun();
        f.SeedStep(state: "running", sessionId: "s");
        foreach (var open in new[] { Question() with { Kind = "permission" }, Question() with { RunId = "r", StepId = "step" } })
            Assert.Equal("bad_args", f.Apply(open, "n", actor: new(ActorKind.Session, "s")).Error);
        Assert.True(OpenProvider(f, "n", "permission").Applied);
    }

    [Theory]
    [InlineData("answered", "\"yes\"", "Jesse answered your question n: yes")]
    [InlineData("answered", "{ \"ok\" : true, \"n\" : 2 }", "Jesse answered your question n: {\"ok\":true,\"n\":2}")]
    [InlineData("dismissed", null, "Jesse dismissed your question n.")]
    [InlineData("expired", null, "Your question n closed without an answer (expired).")]
    [InlineData("cancelled", null, "Your question n closed without an answer (cancelled).")]
    public void Resolution_inserts_one_final_revision_and_replays_after_reopen(string state, string? json, string expected)
    {
        using var f = new EngineFixture(); f.SeedSession(); Assert.True(f.Apply(Question(), "n", actor: Adapter).Applied);
        f.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var command = new ResolveNeed(state, json is null ? null : JsonDocument.Parse(json).RootElement.Clone(), state == "cancelled" ? "policy" : null);
        var actor = state is "expired" or "cancelled" ? Adapter : new EngineActor(ActorKind.Window, "window");
        var commandId = Guid.NewGuid();
        var result = f.Apply(command, "n", 1, actor: actor, commandId: commandId);
        Assert.True(result.Applied); Assert.Equal(1, f.Count("engine_message"));
        Assert.Equal(expected, f.Text("SELECT text FROM engine_message"));
        Assert.Null(f.Text("SELECT from_session_id FROM engine_message"));
        Assert.Equal("system", f.Text("SELECT sender_kind FROM engine_message"));
        Assert.Equal("Assembly", f.Text("SELECT sender_label FROM engine_message"));
        Assert.Equal("prompt", f.Text("SELECT mode FROM engine_message"));
        Assert.Equal("queued", f.State("engine_message", "need-answer/n"));
        Assert.Equal(2, f.Number("SELECT version FROM engine_message"));
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message"));
        Assert.Equal(2000, f.Number("SELECT created_ms FROM engine_message"));
        Assert.Equal(2000, f.Number("SELECT updated_ms FROM engine_message"));
        var e = Assert.Single(f.Events(), e => e.EntityKind == "message"); Assert.Equal(2, e.Version);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        using var payload = JsonDocument.Parse(f.Text("SELECT payload_json FROM engine_outbox WHERE kind='send'")!);
        Assert.Equal(f.Number("SELECT version FROM engine_session WHERE id='s'"), payload.RootElement.GetProperty("recipientVersion").GetInt64());
        f.Reopen();
        Assert.Equal(result, f.Apply(command, "n", 1, actor: actor, commandId: commandId));
        Assert.Equal("already_resolved", f.Apply(command, "n", 2, actor: actor).Error);
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Other_open_need_holds_reply_until_last_resolution()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(Question(), "a", actor: Adapter).Applied);
        Assert.True(OpenProvider(f, "b").Applied);
        Assert.True(f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "a", 1).Applied);
        Assert.Equal(1, f.Number("SELECT version FROM engine_message")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.True(AnswerProvider(f, "b").Applied);
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(2, f.Number("SELECT version FROM engine_message"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Provider_and_ended_need_create_no_synthetic_reply(bool ended)
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True((ended ? f.Apply(Question(), "n", actor: Adapter) : OpenProvider(f, "n")).Applied);
        if (ended) Assert.True(f.Apply(new SetSessionState("ended", "done", null), "s", f.Number("SELECT version FROM engine_session"), actor: Adapter).Applied);
        if (ended)
        {
            Assert.Equal("cancelled", f.State("engine_needs_me", "n"));
            var late = f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "n", 1);
            Assert.False(late.Applied);
            Assert.Equal("already_resolved", late.Error);
            Assert.Equal("cancelled", f.State("engine_needs_me", "n"));
        }
        else Assert.True(AnswerProvider(f, "n").Applied);
        Assert.Equal(0, f.Count("engine_message"));
    }

    [Fact]
    public void Reply_effect_failure_rolls_back_resolution_message_events_and_receipt()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.Apply(Question(), "n", actor: Adapter);
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        f.Db.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_send AFTER INSERT ON engine_outbox BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Throws<SqliteException>(() => f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "n", 1));
        Assert.Equal("open", f.State("engine_needs_me", "n")); Assert.Equal("needs_me", f.State("engine_session", "s"));
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Choice_boundary_counts_are_accepted(int count)
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        var options = JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => i.ToString()).ToArray());
        Assert.False((await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", "{\"question\":\"Pick\",\"kind\":\"choice\",\"options\":" + options + "}")).IsError);
        Assert.Equal("choice", h.Fixture.Text("SELECT kind FROM engine_needs_me"));
    }

    [Theory]
    [InlineData(1200, false)]
    [InlineData(1201, true)]
    public async Task Question_length_boundary_is_enforced(int length, bool refused)
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        var args = "{\"question\":\"" + new string('q', length) + "\"}";
        Assert.Equal(refused, (await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", args)).IsError == true);
        Assert.Equal(refused ? 0 : 1, h.Fixture.Count("engine_needs_me"));
    }

    [Fact]
    public async Task Need_insert_failure_rolls_back_gate_receipt_events_and_publication()
    {
        await using var h = new Harness(); h.Fixture.SeedSession();
        var published = 0;
        using var subscription = h.Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref published));
        h.Fixture.Db.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_need AFTER INSERT ON engine_needs_me BEGIN SELECT RAISE(ABORT,'injected'); END"));
        await Assert.ThrowsAsync<SqliteException>(() => h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", "{\"question\":\"Proceed?\"}"));
        Assert.Equal(0, h.Fixture.Count("engine_needs_me")); Assert.Equal(0, h.Fixture.Count("engine_command_receipt"));
        Assert.Empty(h.Fixture.Events()); Assert.Equal("idle", h.Fixture.State("engine_session", "s")); Assert.Equal(0, published);
    }

    [Fact]
    public void Older_queued_message_releases_first_and_new_reply_keeps_initial_revision()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.Apply(Question(), "n", actor: Adapter);
        Assert.True(f.Apply(new Assembly.Engine.Messages.SendMessage(null, "s", "older", "prompt"), "older").Applied);
        Assert.True(f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")), "n", 1).Applied);
        Assert.Equal(2, f.Number("SELECT version FROM engine_message WHERE id='older'"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_message WHERE id='need-answer/n'"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='need-answer/n'"));
        Assert.Equal(1, Assert.Single(f.Events(), e => e.EntityId == "need-answer/n").Version);
        Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Caller_and_window_answer_use_the_need_sessions_epoch()
    {
        await using var h = new Harness(); h.Fixture.SeedSession(epoch: 7);
        var result = await h.Call(new AskOwnerTool(h.Gate, h.Needs), "s", "{\"question\":\"Proceed?\"}", epoch: 7);
        Assert.False(result.IsError);
        var id = h.Fixture.Text("SELECT id FROM engine_needs_me")!;
        var command = new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes"));
        Assert.Equal("stale_epoch", h.Fixture.Apply(command, id, 1).Error);
        Assert.Equal(0, h.Fixture.Count("engine_message"));
        Assert.True(h.Fixture.Apply(command, id, 1, epoch: 7).Applied);
        Assert.Equal(7, Assert.Single(h.Fixture.Events(), e => e.EntityKind == "message").Data.Epoch);
    }

    private static CommandResult OpenProvider(EngineFixture f, string id, string kind = "question")
    {
        var options = kind == "permission" ? new[] { new AskOption("allow", "Allow", "allow_once") } : [];
        var card = new AskPayload(kind, "open", null, "Proceed?", null, options,
            kind == "question" ? JsonSerializer.SerializeToElement(new { type = "object" }) : null, null);
        var prepared = EngineCommands.PrepareNeedCommand(new OpenNeed("s", null, null, kind, "Proceed?",
            options.Select(option => option.OptionId).ToArray(), "provider", id, 2000,
            JsonSerializer.SerializeToElement(card, RowJsonContext.Rows.AskPayload)));
        CommandResult result = null!;
        RowStore.Open(f.Db).Commit("s", new RowConverter().Apply(new AskChanged(1000, id, card)), scope =>
        {
            result = new EngineCommands(f.Machine, f.Limits).ApplyPreparedNeed(scope,
                new(Guid.NewGuid(), f.Machine, Adapter, id, 0, 0, 1000), prepared);
        });
        return result;
    }

    private static CommandResult AnswerProvider(EngineFixture f, string id)
    {
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { } });
        var captured = new PreparedProviderAnswer(new("s", id, "provider", new("s", null, null, null, 0), "acp"), answer);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Machine, id, 1, 0,
            new ResolveNeed("answered", answer), captured, null, default);
        return f.Db.Writer.Write("test.owner-answer", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyPreparedOwnerNeed(scope, prepared, f.Time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class Harness : IAsyncDisposable
    {
        public Topics Topics { get; } = new();
        public EngineFixture Fixture { get; }
        private readonly EngineRuntime _runtime;
        public McpGate Gate { get; }
        public INeedAnswers Needs { get; } = new UnexpectedNeeds();

        public Harness()
        {
            var limits = new SessionLimits(() => 1, () => 5);
            Fixture = new(topics: Topics, limits: limits);
            _runtime = EngineRuntime.Start(Fixture.Db, Topics, Fixture.Time, limits);
            Gate = new(_runtime, limits, Fixture.Time);
        }

        public async Task<CallToolResult> Call(McpServerTool tool, string session, string arguments, long epoch = 0)
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
            var values = JsonSerializer.Deserialize(arguments, McpJson.Default.DictionaryStringJsonElement)!;
            var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(1) },
                new() { Name = tool.ProtocolTool.Name, Arguments = values })
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", session), new("assembly:epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test")) };
            using var captured = McpRequestWait.Capture(request, CancellationToken.None);
            return await tool.InvokeAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync();
            Fixture.Dispose();
        }
    }

    private sealed class UnexpectedNeeds : INeedAnswers
    {
        public Task<CommandResult> ApplyAsync(McpCaller caller, ParentNeedRequest request, CancellationToken workCancellation, CancellationToken waitCancellation) =>
            throw new InvalidOperationException("An ordinary own question must not invoke the routed-needs service.");
    }
}
