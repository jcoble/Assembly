using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TempDatabase _db = new();
    // Frozen unless a test moves it, so no clock tick fires by surprise.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly RowStore _store;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly FakeLauncher _launcher = new();
    private readonly SharedAdapter _adapter;
    private readonly SessionOpener _opener;
    private readonly SessionInfo _session;
    private readonly SessionHost _host;
    private readonly LiveSubscription _live;
    private readonly StringWriter _log = new();

    public SessionHostTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
        _hub = new LiveHub(_store);
        _sessionStore = SessionStore.Open(_database, _time);
        _session = _sessionStore.Create("claude", "https://github.com/o/r");
        _adapter = new SharedAdapter(_launcher, "claude", _time);
        _opener = new SessionOpener(_sessionStore, _launcher, _ => _adapter);
        _host = new SessionHost(_session.SessionId, _hub, _sessionStore, _opener, _time, _log);
        _live = _hub.Subscribe(_session.SessionId, after: 0);
    }

    [Fact]
    public async Task A_turn_is_saved_and_pushed_row_by_row_in_order()
    {
        var runId = _host.Send("hi");
        Assert.NotNull(runId);
        Assert.Equal(runId, _host.ActiveRunId);
        Assert.Equal(["user", "turn"], await Kinds(2));

        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync();
        Assert.Equal("session/new", open.GetProperty("method").GetString());
        Assert.Equal("/work/session", open.GetProperty("params").GetProperty("cwd").GetString());
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("hi", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());

        await agent.TextAsync("Hello");
        Assert.Equal(["delta"], await Kinds(1));
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn","usage":{"inputTokens":3,"outputTokens":5}}""");

        var end = await Rows(3);
        Assert.Equal(["message", "stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("finished", end[2].Payload.GetProperty("state").GetString());
        Assert.All(end, row => Assert.Equal(runId, row.RunId));
        Assert.Null(_host.ActiveRunId);
        Assert.Equal(FakeLink.AcpSessionId, _sessionStore.Get(_session.SessionId)!.AcpSessionId);
        // The message row replaced its delta.
        Assert.Equal(["user", "turn", "message", "stats", "turn"], _store.RowsAfter(_session.SessionId, 0).Select(row => row.Kind));
        // Once the turn has ended, its session is closed on the adapter.
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
    }

    [Fact]
    public async Task A_turn_logs_how_long_each_stage_of_its_start_took()
    {
        var runId = _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await Kinds(3);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await Kinds(3);
        await agent.CloseAsync();

        // The clock is frozen, so every stage is 0 ms in; the fake launcher marks no clone or adapter start.
        // The line is written from the turn's own task, text first, then the newline.
        await Until(() => _log.ToString().EndsWith(Environment.NewLine, StringComparison.Ordinal));
        Assert.Equal($"session {_session.SessionId} run {runId}: initialize 0 ms, session new 0 ms, first update 0 ms, answer 0 ms, session close 0 ms{Environment.NewLine}", _log.ToString());
    }

    [Fact]
    public async Task A_second_send_while_a_turn_runs_is_refused()
    {
        Assert.NotNull(_host.Send("one"));
        Assert.Null(_host.Send("two"));
        await Kinds(2);
    }

    [Fact]
    public async Task A_message_sent_while_a_turn_runs_goes_into_that_turn()
    {
        var runId = _host.Send("one");
        await Kinds(2);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Looking");
        await Kinds(1);

        var steering = _host.SteerAsync("also this", CancellationToken.None);
        var steer = await agent.ReadAsync("_session/steering");
        Assert.Equal("also this", steer.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal("promptRequired", steer.GetProperty("params").GetProperty("_meta").GetProperty("steering").GetProperty("idleBehavior").GetString());
        await agent.ReplyAsync(steer, """{"outcome":"injected"}""");

        Assert.Equal(runId, await steering.WaitAsync(Wait));
        var rows = await Rows(2);
        Assert.Equal(["message", "user"], rows.Select(row => row.Kind));
        Assert.All(rows, row => Assert.Equal(runId, row.RunId));
        Assert.Equal("also this", rows[1].Payload.GetProperty("text").GetString());
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_message_that_reaches_a_turn_as_it_ends_starts_the_next_turn()
    {
        var runId = _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");

        var steering = _host.SteerAsync("two", CancellationToken.None);
        var steer = await agent.ReadAsync("_session/steering");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.ReplyAsync(steer, """{"outcome":"promptRequired","reason":"noRunningTurn"}""");
        await agent.CloseAsync();

        var next = await steering.WaitAsync(Wait);
        Assert.NotNull(next);
        Assert.NotEqual(runId, next);
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        Assert.Equal("two", second.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_new_session_on_an_agent_that_takes_a_title_is_titled_from_the_first_message()
    {
        _host.Send("  Fix the login page so the password field keeps its value after a failed sign-in\nmore detail");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canRename: true);

        var rename = await agent.ReadAsync("_session/rename");
        var title = rename.GetProperty("params").GetProperty("title").GetString()!;
        Assert.Equal("Fix the login page so the password field keeps its value af…", title);
        Assert.Equal(SessionOpener.TitleLength, title.Length);
        await agent.UpdateAsync($$"""{"sessionUpdate":"session_info_update","title":"{{title}}"}""");
        await agent.ReplyAsync(rename, "{}");
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();

        await Until(() => _sessionStore.Get(_session.SessionId)?.Title == title);
    }

    [Fact]
    public async Task A_helper_keeps_its_task_as_its_title_whatever_its_framed_prompt_says()
    {
        var helper = _sessionStore.Helper(_session, "codex", "Count the lines");
        var host = new SessionHost(helper.SessionId, _hub, _sessionStore, _opener, _time, _log);
        host.Send("You're a helper session, started by another agent's session.\n\nCount the lines");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canRename: true);

        var rename = await agent.ReadAsync("_session/rename");
        Assert.Equal("Count the lines", rename.GetProperty("params").GetProperty("title").GetString());
        await agent.UpdateAsync("""{"sessionUpdate":"session_info_update","title":"You're a helper session, started by"}""");
        await agent.ReplyAsync(rename, "{}");
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();

        Assert.Equal("Count the lines", _sessionStore.Get(helper.SessionId)!.Title);
    }

    [Fact]
    public async Task A_new_session_titles_its_session_from_the_first_message_on_any_agent()
    {
        _host.Send("Explain the build script\nin detail");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();

        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("Explain the build script", _sessionStore.Get(_session.SessionId)?.Title);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    // Claude Code's shape: the effort levels follow the model.
    // One line: the fake agent writes a message per line.
    private static string Options(string model, string effort, string mode = "default") => $$"""
        [{"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"{{mode}}","options":[{"value":"default","name":"Manual"},{"value":"plan","name":"Plan"}]},
         {"id":"model","name":"Model","category":"model","type":"select","currentValue":"{{model}}","options":[{"value":"opus","name":"Opus 5.5"},{"value":"haiku","name":"Haiku 5.5"}]},
         {"id":"effort","name":"Effort","category":"thought_level","type":"select","currentValue":"{{effort}}","options":[{"value":"low","name":"Low"},{"value":"high","name":"High"}]}]
        """.ReplaceLineEndings("");

    [Fact]
    public async Task A_session_gets_the_session_s_chosen_config_in_the_agent_s_order_and_names_its_model()
    {
        _sessionStore.SetConfigChoice(_session.SessionId, "effort", "high");
        _sessionStore.SetConfigChoice(_session.SessionId, "model", "haiku");
        _sessionStore.SetConfigChoice(_session.SessionId, "mode", "default"); // already so: not sent
        _sessionStore.SetConfigChoice(_session.SessionId, "gone", "x"); // not offered: not sent

        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var model = await agent.ReadAsync("session/set_config_option");
        Assert.Equal("model", model.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("haiku", model.GetProperty("params").GetProperty("value").GetString());
        await agent.ReplyAsync(model, $$"""{"configOptions":{{Options("haiku", "low")}}}""");
        var effort = await agent.ReadAsync("session/set_config_option");
        Assert.Equal("effort", effort.GetProperty("params").GetProperty("configId").GetString());
        await agent.ReplyAsync(effort, $$"""{"configOptions":{{Options("haiku", "high")}}}""");

        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var stats = (await Rows(4)).Single(row => row.Kind == "stats");
        Assert.Equal("Haiku 5.5", stats.Payload.GetProperty("model").GetString());
        var saved = JsonDocument.Parse(_sessionStore.Config(_session.SessionId).Options!).RootElement;
        Assert.Equal("high", saved[2].GetProperty("currentValue").GetString());
        Assert.Contains("\"currentValue\":\"low\"", _sessionStore.AgentConfig("claude")); // what a new session starts with
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_choice_made_mid_turn_goes_to_the_open_session_and_the_agent_s_changes_are_kept()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var prompt = await agent.ReadAsync("session/prompt");

        var choosing = _host.SetConfigAsync("mode", "plan", CancellationToken.None);
        var set = await agent.ReadAsync("session/set_config_option");
        Assert.Equal("plan", set.GetProperty("params").GetProperty("value").GetString());
        await agent.ReplyAsync(set, $$"""{"configOptions":{{Options("opus", "low", "plan")}}}""");
        await choosing.WaitAsync(Wait);
        Assert.Equal("plan", _sessionStore.Config(_session.SessionId).Choices["mode"]);

        // The agent leaves plan mode itself.
        await agent.UpdateAsync($$"""{"sessionUpdate":"config_option_update","configOptions":{{Options("opus", "low")}}}""");
        await Until(() => _sessionStore.Config(_session.SessionId).Options?.Contains("\"currentValue\":\"default\"") == true);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    private const string Permission = """
        {"sessionId":"acp-1","toolCall":{"toolCallId":"t1","title":"rm -rf build"},"options":[
          {"optionId":"yes","name":"Allow","kind":"allow_once"},{"optionId":"always","name":"Always allow","kind":"allow_always"},
          {"optionId":"no","name":"Reject","kind":"reject_once"}]}
        """;

    [Fact]
    public async Task A_permission_prompt_is_a_card_answered_with_one_of_the_agent_s_options()
    {
        _host.Send("clean up");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);

        await agent.RequestAsync(7, "session/request_permission", Permission.ReplaceLineEndings(""));
        var card = (await Rows(1)).Single();
        Assert.Equal("ask", card.Kind);
        Assert.Equal("open", card.Payload.GetProperty("state").GetString());
        Assert.Equal("rm -rf build", card.Payload.GetProperty("title").GetString());
        Assert.Equal(["allow_once", "allow_always", "reject_once"],
            card.Payload.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("kind").GetString()));

        var askId = card.ItemId!["ask:".Length..];
        Assert.False(_host.Answer(askId, JsonDocument.Parse("""{"optionId":"maybe"}""").RootElement));
        Assert.True(_host.Answer(askId, JsonDocument.Parse("""{"optionId":"no"}""").RootElement));
        var result = await agent.ResultAsync();
        Assert.Equal("selected", result.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("no", result.GetProperty("outcome").GetProperty("optionId").GetString());
        var settled = (await Rows(1)).Single();
        Assert.Equal(card.ItemId, settled.ItemId);
        Assert.Equal("answered", settled.Payload.GetProperty("state").GetString());
        Assert.Equal("no", settled.Payload.GetProperty("answer").GetProperty("optionId").GetString());
        Assert.False(_host.Answer(askId, JsonDocument.Parse("""{"optionId":"yes"}""").RootElement));

        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Stopping_the_turn_settles_its_open_card_as_cancelled()
    {
        var runId = _host.Send("clean up")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);
        await agent.RequestAsync(7, "session/request_permission", Permission.ReplaceLineEndings(""));
        await Rows(1);

        Assert.True(_host.Stop(runId));
        // The cancel and the prompt's answer may go in either order.
        var sent = new[] { await agent.NextMessageAsync(), await agent.NextMessageAsync() };
        Assert.Single(sent, message => message.TryGetProperty("method", out var method) && method.GetString() == "session/cancel");
        var result = sent.Single(message => message.TryGetProperty("result", out _)).GetProperty("result");
        Assert.Equal("cancelled", result.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("cancelled", (await Rows(1)).Single().Payload.GetProperty("state").GetString());
        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Losing_the_adapter_settles_its_open_card_as_cancelled()
    {
        _host.Send("clean up");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await Kinds(2);
        await agent.RequestAsync(7, "session/request_permission", Permission.ReplaceLineEndings(""));
        await Rows(1);

        agent.Crash();
        var rows = await Rows(4);
        Assert.Equal("cancelled", rows.Single(row => row.Kind == "ask").Payload.GetProperty("state").GetString());
        Assert.Contains(rows, row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed");
    }

    [Fact]
    public async Task A_question_with_choices_is_a_card_whose_form_answer_goes_back_to_the_agent()
    {
        _host.Send("set it up");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);

        await agent.RequestAsync(8, "elicitation/create", """
            {"sessionId":"acp-1","mode":"form","toolCallId":"t2","message":"Which database?","requestedSchema":{"type":"object","properties":{
              "question_0":{"type":"string","oneOf":[{"const":"Postgres","title":"Postgres"},{"const":"SQLite","title":"SQLite"}]},
              "question_0_custom":{"type":"string","title":"Other"}}}}
            """.ReplaceLineEndings(""));
        var card = (await Rows(1)).Single();
        Assert.Equal("question", card.Payload.GetProperty("kind").GetString());
        Assert.Equal("Which database?", card.Payload.GetProperty("message").GetString());
        Assert.Equal("SQLite", card.Payload.GetProperty("schema").GetProperty("properties").GetProperty("question_0").GetProperty("oneOf")[1].GetProperty("const").GetString());

        Assert.True(_host.Answer(card.ItemId!["ask:".Length..], JsonDocument.Parse("""{"action":"accept","content":{"question_0":"SQLite"}}""").RootElement));
        var result = await agent.ResultAsync();
        Assert.Equal("accept", result.GetProperty("action").GetString());
        Assert.Equal("SQLite", result.GetProperty("content").GetProperty("question_0").GetString());
        Assert.Equal("answered", (await Rows(1)).Single().Payload.GetProperty("state").GetString());

        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }
    // Rows up to and including the first that matches.
    private async Task<List<StoredRow>> RowsUntil(Func<StoredRow, bool> last)
    {
        var rows = new List<StoredRow>();
        do
            rows.Add(await _live.Rows.ReadAsync().AsTask().WaitAsync(Wait));
        while (!last(rows[^1]));
        return rows;
    }

    private async Task<List<StoredRow>> Rows(int count)
    {
        var rows = new List<StoredRow>();
        while (rows.Count < count)
            rows.Add(await _live.Rows.ReadAsync().AsTask().WaitAsync(Wait));
        return rows;
    }

    // Answers the turn's prompt after reporting the session's cost so far.
    private static async Task CostAsync(FakeLink agent, decimal sessionCost)
    {
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync($$$"""{"sessionUpdate":"usage_update","used":1,"size":10,"cost":{"amount":{{{sessionCost}}},"currency":"USD"}}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
    }

    private async Task<JsonElement> Stats()
    {
        while (true)
        {
            var row = await _live.Rows.ReadAsync().AsTask().WaitAsync(Wait);
            if (row.Kind == "stats")
                return row.Payload;
        }
    }

    private static async Task<string> MessageTextAsync(LiveSubscription live)
    {
        while (true)
        {
            var row = await live.Rows.ReadAsync().AsTask().WaitAsync(Wait);
            if (row.Kind == "message")
                return row.Payload.GetProperty("blocks")[0].GetProperty("parts")[0].GetProperty("value").GetString()!;
        }
    }

    private static async Task<string> ErrorCodeAsync(LiveSubscription live)
    {
        while (true)
        {
            var row = await live.Rows.ReadAsync().AsTask().WaitAsync(Wait);
            if (row.Kind == "error")
                return row.Payload.GetProperty("code").GetString()!;
        }
    }

    private async Task<List<string>> Kinds(int count) => (await Rows(count)).Select(row => row.Kind).ToList();

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        var turn = _host.StopForShutdownAsync();
        await _adapter.DisposeAsync();
        await turn;
        _live.Dispose();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
    }
}
