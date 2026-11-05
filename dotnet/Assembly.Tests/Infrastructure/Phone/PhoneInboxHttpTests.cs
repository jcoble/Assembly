using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Phone;
using Assembly.Tests.Engine.Workflows.Execution;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Phone;

/// <summary>A phone's approvals over real HTTP, against a real run with a gate and a command approval both open.</summary>
public sealed class PhoneInboxHttpTests
{
    [Fact]
    public async Task A_phone_is_offered_a_gate_with_words_it_can_show()
    {
        await using var phone = await PhoneInbox.OpenAsync();
        var gate = await phone.ItemAsync(phone.GateNeed);
        Assert.Equal("gate", gate.GetProperty("kind").GetString());
        var options = gate.GetProperty("options").EnumerateArray().ToArray();
        Assert.Equal(new[] { "approve", "revise", "deny" }, options.Select(option => option.GetProperty("id").GetString()));
        Assert.Equal(new[] { "Approve", "Revise", "Deny" }, options.Select(option => option.GetProperty("label").GetString()));
        Assert.Equal(new[] { "primary", "neutral", "danger" }, options.Select(option => option.GetProperty("style").GetString()));
        Assert.True(options[1].GetProperty("needsText").GetBoolean());
        Assert.False(options[0].TryGetProperty("needsText", out _));
        Assert.Equal("", gate.GetProperty("detail").GetString());
        Assert.Equal("approve", gate.GetProperty("stepId").GetString());
        Assert.Empty(gate.GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public async Task A_phone_is_offered_a_command_approval_with_words_it_can_show()
    {
        await using var phone = await PhoneInbox.OpenAsync();
        var command = await phone.ItemAsync(phone.CommandNeed);
        Assert.Equal("permission", command.GetProperty("kind").GetString());
        var options = command.GetProperty("options").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Allow once", "Deny" }, options.Select(option => option.GetProperty("label").GetString()));
        Assert.Equal(new[] { "primary", "danger" }, options.Select(option => option.GetProperty("style").GetString()));
        Assert.Equal("checks", command.GetProperty("stepId").GetString());
        Assert.Empty(command.GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public async Task A_phone_approves_a_gate_and_a_second_answer_is_stale()
    {
        await using var phone = await PhoneInbox.OpenAsync();
        using var approved = await phone.AnswerAsync(phone.GateNeed, new { optionId = "approve", version = 1 });
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal("answered", phone.NeedState(phone.GateNeed));
        Assert.Equal("succeeded", phone.StepState(phone.GateNeed));
        using var again = await phone.AnswerAsync(phone.GateNeed, new { optionId = "deny", version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task A_phone_revises_a_gate_and_its_text_goes_to_the_work_before_it()
    {
        await using var phone = await PhoneInbox.OpenAsync();
        using var revised = await phone.AnswerAsync(phone.GateNeed, new { optionId = "revise", version = 1, text = "Smaller, from the phone" });
        Assert.Equal(HttpStatusCode.NoContent, revised.StatusCode);
        Assert.Equal("Smaller, from the phone", phone.Text("SELECT json_extract(safe_body_json,'$.text') FROM engine_workflow_history WHERE kind='gate_revised'"));
        Assert.Equal("superseded", phone.NeedState(phone.CommandNeed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_revision_with_nothing_to_say_is_refused_and_the_gate_stays_open(string? text)
    {
        await using var phone = await PhoneInbox.OpenAsync();
        using var refused = await phone.AnswerAsync(phone.GateNeed, new { optionId = "revise", version = 1, text });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("open", phone.NeedState(phone.GateNeed));
    }

    [Fact]
    public async Task A_late_answer_to_a_closed_command_approval_is_stale_and_says_why()
    {
        await using var phone = await PhoneInbox.OpenAsync();
        using var revised = await phone.AnswerAsync(phone.GateNeed, new { optionId = "revise", version = 1, text = "Smaller" });
        Assert.Equal(HttpStatusCode.NoContent, revised.StatusCode);
        using var late = await phone.AnswerAsync(phone.CommandNeed, new { optionId = "allow_once", version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var body = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", body.GetProperty("error").GetString());
        Assert.Equal("Closed: the work it follows is being redone", body.GetProperty("detail").GetString());
        Assert.DoesNotContain(await phone.IdsAsync(), id => id == phone.CommandNeed);
    }

    private sealed class PhoneInbox : IAsyncDisposable
    {
        private readonly CommandStepTests.Fixture _fixture;
        private readonly EngineRuntime _runtime;
        private readonly LiveHub _hub;
        private readonly BackgroundWork _work;
        private readonly SessionRegistry _hosts;
        private readonly NeedAnswers _answers;
        private readonly WebApplication _app;
        private readonly HttpClient _http;

        public string CommandNeed { get; }
        public string GateNeed { get; }

        private PhoneInbox(CommandStepTests.Fixture fixture, EngineRuntime runtime, LiveHub hub, BackgroundWork work, SessionRegistry hosts,
            NeedAnswers answers, WebApplication app, HttpClient http, string commandNeed, string gateNeed)
        {
            (_fixture, _runtime, _hub, _work, _hosts, _answers, _app, _http) = (fixture, runtime, hub, work, hosts, answers, app, http);
            (CommandNeed, GateNeed) = (commandNeed, gateNeed);
        }

        public static async Task<PhoneInbox> OpenAsync()
        {
            var f = CommandStepTests.ApprovalBesideCommand();
            var (commandNeed, gateNeed, _, _) = await CommandStepTests.BothAsking(f, settled: true);
            var core = f.Core;
            var runtime = EngineRuntime.Start(core.Db, new Topics(), core.Time, core.Limits);
            var hub = new LiveHub(RowStore.Open(core.Db));
            var work = new BackgroundWork(core.Time, TextWriter.Null);
            var hosts = new SessionRegistry(hub, SessionStore.Open(core.Db, core.Time, "/isolated"), new FakeLauncher(), core.Time, TextWriter.Null, work);
            var answers = new NeedAnswers(new McpGate(runtime, core.Limits, core.Time), runtime, hosts, core.Time);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            PhoneNeedsRoutes.Map(app, _ => new PhonePeer("device"), runtime, answers, (_, _) => { });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(f, runtime, hub, work, hosts, answers, app, new() { BaseAddress = new(address) }, commandNeed, gateNeed);
        }

        public async Task<JsonElement> ItemAsync(string id)
        {
            var inbox = await _http.GetFromJsonAsync<JsonElement>("/phone/v1/inbox");
            return inbox.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        }

        public async Task<string?[]> IdsAsync()
        {
            var inbox = await _http.GetFromJsonAsync<JsonElement>("/phone/v1/inbox");
            return inbox.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        }

        public Task<HttpResponseMessage> AnswerAsync(string id, object body) =>
            _http.PostAsJsonAsync($"/phone/v1/inbox/{Uri.EscapeDataString(id)}/answer", body);

        public string? Text(string sql) => _fixture.Core.Text(sql);

        public string? NeedState(string id) => _fixture.Core.Text("SELECT state FROM engine_needs_me WHERE id=$id", new SqlArg("$id", id));

        public string? StepState(string need) => _fixture.Core.Text(
            "SELECT st.state FROM engine_step st JOIN engine_needs_me n ON n.step_id=st.id WHERE n.id=$id", new SqlArg("$id", need));

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.DisposeAsync(); await _answers.DisposeAsync();
            await _runtime.DisposeAsync();
            await _hosts.DisposeAsync(); await _work.DisposeAsync(); _hub.Dispose();
            _fixture.Dispose();
        }
    }
}
