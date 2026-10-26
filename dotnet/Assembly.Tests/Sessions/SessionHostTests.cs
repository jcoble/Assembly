using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Assembly.Engine.Agents;
using Assembly.Engine.Live;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Tests.Sessions;

public sealed class SessionHostTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TempDatabase _db = new();
    // Frozen unless a test moves it, so no clock tick fires by surprise.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly RowStore _store;
    private readonly LiveHub _hub;
    private readonly ThreadStore _threads;
    private readonly FakeLauncher _launcher = new();
    private readonly SharedAdapter _adapter;
    private readonly SessionOpener _opener;
    private readonly ThreadInfo _thread;
    private readonly SessionHost _host;
    private readonly LiveSubscription _live;
    private readonly StringWriter _log = new();

    public SessionHostTests()
    {
        _database = AppDatabase.Open(_db.Path);
        _store = RowStore.Open(_database);
        _hub = new LiveHub(_store);
        _threads = ThreadStore.Open(_database, _time);
        _thread = _threads.Create("claude", "https://github.com/o/r");
        _adapter = new SharedAdapter(_launcher, "claude", _time);
        _opener = new SessionOpener(_threads, _launcher, _ => _adapter);
        _host = new SessionHost(_thread.ThreadId, _hub, _threads, _opener, _time, _log);
        _live = _hub.Subscribe(_thread.ThreadId, after: 0);
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
        Assert.Equal("/work/thread", open.GetProperty("params").GetProperty("cwd").GetString());
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
        Assert.Equal(FakeLink.SessionId, _threads.Get(_thread.ThreadId)!.AcpSessionId);
        // The message row replaced its delta.
        Assert.Equal(["user", "turn", "message", "stats", "turn"], _store.RowsAfter(_thread.ThreadId, 0).Select(row => row.Kind));
        // Once the turn has ended, its session is closed on the adapter.
        Assert.Equal(FakeLink.SessionId, await agent.CloseAsync());
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
        Assert.Equal($"thread {_thread.ThreadId} run {runId}: initialize 0 ms, session new 0 ms, first update 0 ms, answer 0 ms, session close 0 ms{Environment.NewLine}", _log.ToString());
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

        await Until(() => _threads.Get(_thread.ThreadId)?.Title == title);
    }

    [Fact]
    public async Task A_helper_keeps_its_task_as_its_title_whatever_its_framed_prompt_says()
    {
        var helper = _threads.Helper(_thread, "codex", "Count the lines");
        var host = new SessionHost(helper.ThreadId, _hub, _threads, _opener, _time, _log);
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

        Assert.Equal("Count the lines", _threads.Get(helper.ThreadId)!.Title);
    }

    [Fact]
    public async Task A_new_session_titles_its_thread_from_the_first_message_on_any_agent()
    {
        _host.Send("Explain the build script\nin detail");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();

        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("Explain the build script", _threads.Get(_thread.ThreadId)?.Title);
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
    public async Task A_session_gets_the_thread_s_chosen_config_in_the_agent_s_order_and_names_its_model()
    {
        _threads.SetConfigChoice(_thread.ThreadId, "effort", "high");
        _threads.SetConfigChoice(_thread.ThreadId, "model", "haiku");
        _threads.SetConfigChoice(_thread.ThreadId, "mode", "default"); // already so: not sent
        _threads.SetConfigChoice(_thread.ThreadId, "gone", "x"); // not offered: not sent

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
        var saved = JsonDocument.Parse(_threads.Config(_thread.ThreadId).Options!).RootElement;
        Assert.Equal("high", saved[2].GetProperty("currentValue").GetString());
        Assert.Contains("\"currentValue\":\"low\"", _threads.AgentConfig("claude")); // what a new session starts with
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
        Assert.Equal("plan", _threads.Config(_thread.ThreadId).Choices["mode"]);

        // The agent leaves plan mode itself.
        await agent.UpdateAsync($$"""{"sessionUpdate":"config_option_update","configOptions":{{Options("opus", "low")}}}""");
        await Until(() => _threads.Config(_thread.ThreadId).Options?.Contains("\"currentValue\":\"default\"") == true);
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

    [Fact]
    public async Task An_agent_that_does_not_steer_refuses_a_message_mid_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canSteer: false);
        await agent.ReadAsync("session/prompt");

        Assert.Null(await _host.SteerAsync("two", CancellationToken.None));
    }

    [Fact]
    public async Task An_update_that_comes_after_its_session_closed_never_reaches_the_next_turn()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Kinds(4);
        await Until(() => _host.ActiveRunId is null);

        await agent.TextAsync("late", messageId: "late");
        await Task.Delay(200); // for the client to read it
        _host.Send("two");
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("fresh", messageId: "fresh");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");

        var rows = await Rows(6);
        Assert.Equal(["user", "turn", "delta", "message", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.All(rows.Where(row => row.ItemId is not null), row => Assert.Equal("fresh", row.ItemId));
    }

    [Fact]
    public async Task The_adapter_stops_once_it_has_had_no_session_for_the_idle_grace()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Until(() => _host.ActiveRunId is null);

        // Inside the grace the next turn finds it warm.
        _time.Advance(SharedAdapter.IdleGrace - TimeSpan.FromSeconds(1));
        _host.Send("two");
        await agent.OpenAsync();
        var second = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Until(() => _host.ActiveRunId is null);
        Assert.False(agent.Disposed);

        // The grace starts once the close's answer is in, so the clock moves on in steps until the adapter stops.
        var waited = TimeSpan.Zero;
        await Until(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            waited += TimeSpan.FromSeconds(5);
            return agent.Disposed;
        });
        Assert.True(waited >= SharedAdapter.IdleGrace);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task Text_held_back_goes_out_on_the_next_clock_tick()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await Kinds(2);

        await agent.TextAsync("a");
        var first = (await Rows(1))[0];
        Assert.Equal(1_000_000, first.CreatedAt);

        _time.Advance(TimeSpan.FromMilliseconds(10));
        await agent.TextAsync("b");
        // The title is a marker: once it's saved, "b" has been read and held.
        await agent.UpdateAsync("""{"sessionUpdate":"session_info_update","title":"marker"}""");
        await Until(() => _threads.Get(_thread.ThreadId)!.Title == "marker");
        Assert.False(_live.Rows.TryRead(out _));

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var held = (await Rows(1))[0];
        Assert.Equal("delta", held.Kind);
        Assert.Equal("b", held.Payload.GetProperty("text").GetString());
        Assert.Equal(1_000_110, held.CreatedAt);
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_ends_it_and_the_next_send_resumes_its_session()
    {
        var runId = _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"Terminal","kind":"execute","status":"pending","rawInput":{"command":"sleep 60"}}""");
        await Kinds(3);

        agent.Crash();
        var end = await Rows(4);
        Assert.Equal(["tool", "error", "stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("failed", end[0].Payload.GetProperty("state").GetString());
        Assert.Equal("agent_lost", end[1].Payload.GetProperty("code").GetString());
        Assert.Equal("failed", end[3].Payload.GetProperty("state").GetString());
        Assert.Null(_host.ActiveRunId);

        // The next send finds the adapter gone, ends it, and starts a new one, which resumes the session.
        Assert.NotNull(_host.Send("again"));
        var second = await _launcher.NextAsync();
        var open = await second.StartAsync();
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        Assert.Equal(FakeLink.SessionId, open.GetProperty("params").GetProperty("sessionId").GetString());
        await second.ReadAsync("session/prompt");
        Assert.True(agent.Disposed);
        Assert.Equal(2, _launcher.Launches);
    }

    [Fact]
    public async Task Each_turn_resumes_the_closed_session_and_costs_what_its_total_grew_past_the_saved_one()
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await CostAsync(agent, 0.20m);
        Assert.Equal(0.20m, (await Stats()).GetProperty("costUsd").GetDecimal());
        await agent.CloseAsync();
        await Until(() => _threads.Get(_thread.ThreadId)!.SessionCost == 0.20m);

        // The same adapter: no new launch and no initialize, just the session reopened. The agent reports its
        // total on from where the closed session left it.
        _host.Send("two");
        var open = await agent.OpenAsync();
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        await CostAsync(agent, 0.32m);
        Assert.Equal(0.12m, (await Stats()).GetProperty("costUsd").GetDecimal());
        await agent.CloseAsync();
        await Until(() => _threads.Get(_thread.ThreadId)!.SessionCost == 0.32m);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task A_resumed_turn_without_a_saved_total_has_no_cost()
    {
        _threads.SetAcpSession(_thread.ThreadId, "earlier");
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        Assert.Equal("session/resume", (await agent.StartAsync()).GetProperty("method").GetString());
        await CostAsync(agent, 0.25m);
        Assert.False((await Stats()).TryGetProperty("costUsd", out _));
    }

    [Fact]
    public async Task Threads_share_one_adapter_initialized_once_and_each_gets_its_own_updates()
    {
        var other = _threads.Create("claude", "https://github.com/o/r");
        var otherHost = new SessionHost(other.ThreadId, _hub, _threads, _opener, _time, _log);
        using var otherLive = _hub.Subscribe(other.ThreadId, after: 0);

        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(sessionId: "acp-1");
        var first = await agent.ReadAsync("session/prompt");
        otherHost.Send("two");
        await agent.OpenAsync("acp-2");
        var second = await agent.ReadAsync("session/prompt");
        Assert.Equal("acp-2", second.GetProperty("params").GetProperty("sessionId").GetString());

        await agent.TextAsync("for one", sessionId: "acp-1");
        await agent.TextAsync("for two", sessionId: "acp-2");
        await agent.ReplyAsync(second, """{"stopReason":"end_turn"}""");
        await agent.ReplyAsync(first, """{"stopReason":"end_turn"}""");

        Assert.Equal(1, _launcher.Launches);
        Assert.Equal("for one", await MessageTextAsync(_live));
        Assert.Equal("for two", await MessageTextAsync(otherLive));
        Assert.Equal(["acp-1", "acp-2"], new[] { await agent.CloseAsync(), await agent.CloseAsync() }.Order());
        await otherHost.StopForShutdownAsync();
    }

    [Fact]
    public async Task When_the_shared_adapter_dies_every_turn_on_it_ends_as_lost()
    {
        var other = _threads.Create("claude", "https://github.com/o/r");
        var otherHost = new SessionHost(other.ThreadId, _hub, _threads, _opener, _time, _log);
        using var otherLive = _hub.Subscribe(other.ThreadId, after: 0);
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(sessionId: "acp-1");
        await agent.ReadAsync("session/prompt");
        otherHost.Send("two");
        await agent.OpenAsync("acp-2");
        await agent.ReadAsync("session/prompt");

        agent.Crash();
        Assert.Equal("agent_lost", await ErrorCodeAsync(_live));
        Assert.Equal("agent_lost", await ErrorCodeAsync(otherLive));
        await otherHost.StopForShutdownAsync();
    }

    [Fact]
    public async Task An_agent_that_cannot_resume_gets_a_new_session()
    {
        _threads.SetAcpSession(_thread.ThreadId, "old");
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync(canResume: false);

        Assert.Equal("session/new", open.GetProperty("method").GetString());
        await agent.ReadAsync("session/prompt");
        Assert.Equal(FakeLink.SessionId, _threads.Get(_thread.ThreadId)!.AcpSessionId);
    }

    [Fact]
    public async Task Stop_cancels_the_running_turn()
    {
        var runId = _host.Send("hi")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await Kinds(2);

        Assert.False(_host.Stop("some-other-run"));
        Assert.True(_host.Stop(runId));
        var cancel = await agent.ReadAsync("session/cancel");
        Assert.Equal(FakeLink.SessionId, cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await agent.ReplyAsync(prompt, """{"stopReason":"cancelled"}""");

        var end = await Rows(2);
        Assert.Equal("aborted", end[0].Payload.GetProperty("status").GetString());
        Assert.Equal("cancelled", end[1].Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Stop_before_the_agent_is_up_cancels_without_a_prompt()
    {
        var launching = new TaskCompletionSource<IAgentLink>();
        _launcher.Launch = cancellationToken =>
        {
            cancellationToken.Register(() => launching.TrySetCanceled(cancellationToken));
            return launching.Task;
        };
        var runId = _host.Send("hi")!;
        await Kinds(2);

        Assert.True(_host.Stop(runId));
        var end = await Rows(2);
        Assert.Equal(["stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("cancelled", end[1].Payload.GetProperty("state").GetString());
        Assert.Null(_host.ActiveRunId);
    }

    [Fact]
    public async Task A_failed_launch_fails_the_turn_with_its_reason()
    {
        _launcher.Launch = _ => throw new InvalidOperationException("git clone failed: repository not found");
        _host.Send("hi");

        var rows = await Rows(5);
        Assert.Equal(["user", "turn", "error", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.Equal("git clone failed: repository not found", rows[2].Payload.GetProperty("message").GetString());
        Assert.Equal("failed", rows[4].Payload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_prompt_error_fails_the_turn()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyErrorAsync(prompt, -32603, "Internal error");

        var rows = await Rows(5);
        Assert.Equal(["user", "turn", "error", "stats", "turn"], rows.Select(row => row.Kind));
        Assert.Equal("-32603", rows[2].Payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_agents_title_goes_to_the_sessions_list()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"session_info_update","title":"Say hello"}""");

        await Until(() => _threads.Get(_thread.ThreadId)!.Title == "Say hello");
    }

    [Fact]
    public async Task The_agents_slash_commands_are_saved_for_the_thread()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"context","description":"Show context usage","input":null}]}""");

        await Until(() => _threads.Config(_thread.ThreadId).Commands?.Contains("\"context\"") == true);
    }

    [Fact]
    public async Task Background_work_keeps_the_session_open_and_what_the_agent_does_after_it_is_a_run_of_its_own()
    {
        var (agent, runId) = await StartClaudeBackgroundAsync();
        Assert.Null(_host.ActiveRunId);
        Assert.Contains("session kept open for background work", _log.ToString());

        // With no prompt running, the agent goes on by itself: a run of its own, with no message of the user's.
        await agent.TextAsync("It printed finished.", "m2");
        var started = await RowsUntil(row => row.Kind == "delta");
        Assert.Equal(["turn", "delta"], started.Select(row => row.Kind));
        var selfRun = started[0].RunId!;
        Assert.NotEqual(runId, selfRun);
        Assert.Equal("started", started[0].Payload.GetProperty("state").GetString());
        Assert.Equal("Continued after a background task finished", started[0].Payload.GetProperty("reason").GetString());
        Assert.Equal(selfRun, _host.ActiveRunId);

        // Claude's tag on the cycle's usage ends the run, and the untracked task with it; then the session closes.
        await agent.UpdateAsync(TaskNotification);
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(["context", "task", "message", "stats", "turn"], end.Select(row => row.Kind));
        Assert.Equal("completed", end[1].Payload.GetProperty("state").GetString());
        Assert.Equal(runId, end[1].RunId);
        Assert.Equal("finished", end[4].Payload.GetProperty("state").GetString());
        Assert.Equal(selfRun, end[4].RunId);
        Assert.Null(_host.ActiveRunId);
        Assert.Equal(FakeLink.SessionId, await agent.CloseAsync());
    }

    [Fact]
    public async Task An_adapter_that_dies_under_a_kept_session_ends_its_background_work()
    {
        var (agent, runId) = await StartClaudeBackgroundAsync();

        agent.Crash();
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal((runId, "stopped"), (task.RunId, task.Payload.GetProperty("state").GetString()));
        // The thread takes sends again, on a new adapter.
        Assert.NotNull(_host.Send("again"));
        await (await _launcher.NextAsync()).StartAsync();
    }

    [Fact]
    public async Task A_message_that_reaches_a_run_the_agent_started_as_it_ends_starts_the_next_turn()
    {
        var (agent, _) = await StartClaudeBackgroundAsync();
        await agent.TextAsync("It finished.", "m2");
        await RowsUntil(row => row.Kind == "delta");

        var steering = _host.SteerAsync("next", CancellationToken.None);
        var steer = await agent.ReadAsync("_session/steering");
        await agent.ReplyAsync(steer, """{"outcome":"promptRequired","reason":"noRunningTurn"}""");
        await agent.UpdateAsync(TaskNotification);
        var next = await steering.WaitAsync(Wait);
        Assert.NotNull(next);
        // The session closed when the run and its task ended, so the next turn resumes it.
        await agent.CloseAsync();
        var prompt = await agent.ReadAsync("session/resume");
        await agent.ReplyAsync(prompt, "{}");
        Assert.Equal("next", (await agent.ReadAsync("session/prompt")).GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_send_while_only_background_work_runs_prompts_the_open_session()
    {
        var (agent, _) = await StartClaudeBackgroundAsync();

        var next = _host.Send("anything new?")!;
        Assert.Equal(["user", "turn"], await Kinds(2));
        // Straight to the prompt: the session is still open, so nothing is opened or resumed.
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(next, end[^1].RunId);

        await agent.UpdateAsync(TaskNotification);
        Assert.Equal("completed", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_backgrounded_command_finishing_after_its_turn_stays_in_that_turn_and_starts_no_run()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"in_progress"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"t1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        // Codex's end of the task names no task, then the tool call it ran in finishes.
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"t1","state":"completed"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed"}""");
        var late = await RowsUntil(row => row.Kind == "tool");
        Assert.Equal(["task", "tool"], late.Select(row => row.Kind));
        Assert.All(late, row => Assert.Equal(runId, row.RunId));
        Assert.Equal("""{"name":"sleep 15","state":"completed","canStop":false,"toolCallId":"t1"}""", late[0].Payload.GetRawText());
        Assert.Null(_host.ActiveRunId);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_task_the_agent_tells_of_just_after_its_answer_still_keeps_the_session()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"in_progress"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"t1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal((runId, "running"), (task.RunId, task.Payload.GetProperty("state").GetString()));
        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.TaskGraceMs));
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"t1","state":"completed"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_turn_that_ends_with_a_tool_running_and_no_task_closes_its_session_after_a_moment()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"ls","kind":"execute","status":"in_progress"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.TaskGraceMs));
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_task_the_agent_can_stop_is_stopped_on_request()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":true,"toolCallId":"t1"}""");
        var task = (await RowsUntil(row => row.Kind == "task"))[^1];
        Assert.Equal("task:x1", task.ItemId);
        Assert.Equal("""{"name":"sleep 15","state":"running","canStop":true,"toolCallId":"t1"}""", task.Payload.GetRawText());

        Assert.False(await _host.StopTaskAsync("some-other-task", CancellationToken.None));
        var stopping = _host.StopTaskAsync("x1", CancellationToken.None);
        var stop = await agent.ReadAsync("_session/async_task/stop");
        Assert.Equal("x1", stop.GetProperty("params").GetProperty("asyncTaskId").GetString());
        await agent.ReplyAsync(stop, """{"stopped":true}""");
        Assert.True(await stopping.WaitAsync(Wait));

        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"x1","state":"cancelled"}""");
        Assert.Equal("stopped", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
        Assert.False(await _host.StopTaskAsync("x1", CancellationToken.None));
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task A_run_the_agent_started_that_goes_quiet_ends_after_a_while()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":false}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        await agent.TextAsync("Waiting on it.", "m2");
        var selfRun = (await RowsUntil(row => row.Kind == "delta"))[0].RunId;
        // The task's end doesn't end the run; the agent never says its cycle is over.
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_state_update","asyncTaskId":"x1","state":"completed"}""");
        await RowsUntil(row => row.Kind == "task");

        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.QuietMs));
        var end = await RowsUntil(row => row.Kind == "turn");
        Assert.Equal(selfRun, end[^1].RunId);
        Assert.Equal("finished", end[^1].Payload.GetProperty("state").GetString());
        Assert.Contains("no word from the agent for 30 s", _log.ToString());
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Shutting_down_stops_background_work_before_closing_its_session()
    {
        _host.Send("start it");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"async_task_spawned","asyncTaskId":"x1","name":"sleep 15","canStop":true}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        var shutdown = _host.CloseKeptForShutdownAsync();
        var stop = await agent.ReadAsync("_session/async_task/stop");
        await agent.ReplyAsync(stop, """{"stopped":true}""");
        await agent.CloseAsync();
        await shutdown.WaitAsync(Wait);
        Assert.Equal("stopped", (await RowsUntil(row => row.Kind == "task"))[^1].Payload.GetProperty("state").GetString());
    }

    private const string TaskNotification =
        """{"sessionUpdate":"usage_update","used":1,"size":10,"_meta":{"_claude/origin":{"kind":"task-notification"}}}""";

    // A turn in which Claude backgrounds a Bash command, through to its end; the task row is still running.
    private const string ChildAnnounced =
        """{"sessionUpdate":"subagent_update","sessionId":"child1","title":"general-purpose","description":"Count lines","capabilities":{"cancel":{}},"state":{"state":"running"}}""";

    private const string ChildPrompt =
        """{"sessionUpdate":"session_message","messageId":"task1","content":[{"type":"text","text":"Count the lines of notes.txt"}]}""";

    [Fact]
    public async Task A_subagent_is_a_thread_of_its_own_nested_under_the_tool_call_that_started_it()
    {
        var runId = _host.Send("count the lines")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"task1","title":"Task","kind":"think","status":"pending"}""");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        await agent.TextAsync("3 lines", "c1", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"task1","status":"completed"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        // Announced, nested once its prompt names the Task call, then idle; all in the run that started it.
        var subagent = rows.Where(row => row.Kind == "subagent").ToList();
        Assert.Equal(["running", "running", "idle"], subagent.Select(row => row.Payload.GetProperty("state").GetString()));
        Assert.All(subagent, row => Assert.Equal((runId, "subagent:child1"), (row.RunId, row.ItemId)));
        Assert.Equal(JsonValueKind.Null, subagent[0].Payload.TryGetProperty("toolCallId", out var none) ? none.ValueKind : JsonValueKind.Null);
        Assert.Equal("task1", subagent[^1].Payload.GetProperty("toolCallId").GetString());
        Assert.False(subagent[^1].Payload.GetProperty("canStop").GetBoolean());
        Assert.True(subagent[0].Payload.GetProperty("canStop").GetBoolean());

        // Its thread is the parent's child, kept out of the list, with its prompt, its reply, and its end.
        var child = _threads.Get(subagent[0].Payload.GetProperty("threadId").GetString()!)!;
        Assert.Equal((_thread.ThreadId, "child1", "general-purpose"), (child.ParentThreadId, child.AcpSessionId, child.Title));
        Assert.DoesNotContain(_threads.List(null, 50).Threads, thread => thread.ThreadId == child.ThreadId);
        var childRows = _store.RowsAfter(child.ThreadId, 0);
        Assert.Equal("Count the lines of notes.txt", childRows.First(row => row.Kind == "user").Payload.GetProperty("text").GetString());
        Assert.Contains(childRows, row => row.Kind == "message");
        Assert.Equal("finished", childRows.Last(row => row.Kind == "turn").Payload.GetProperty("state").GetString());
        Assert.DoesNotContain(_store.RowsAfter(_thread.ThreadId, 0), row => row.Kind is "message" or "delta" && row.ItemId == "c1");
    }

    [Fact]
    public async Task A_subagent_s_handback_report_is_its_reply_not_a_tool_call()
    {
        _host.Send("count the lines");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        // As recorded: the report is the handback call's input; its output only says it was delivered.
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"hb1","title":"SubagentHandback","kind":"other","status":"pending","rawInput":{"message":"notes.txt has 3 lines."},"_meta":{"claudeCode":{"toolName":"SubagentHandback"}}}""", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"hb1","status":"completed","rawOutput":[{"type":"text","text":"{\"success\":true,\"message\":\"Report delivered to your caller.\"}"}]}""", "child1");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"end_turn"}}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");

        var child = rows.First(row => row.Kind == "subagent").Payload.GetProperty("threadId").GetString()!;
        var childRows = _store.RowsAfter(child, 0);
        Assert.DoesNotContain(childRows, row => row.Kind == "tool");
        Assert.Contains("notes.txt has 3 lines.", childRows.Last(row => row.Kind == "message").Payload.GetRawText());
    }

    [Fact]
    public async Task A_subagent_still_at_work_keeps_the_session_open_and_stops_on_its_own()
    {
        _host.Send("count the lines in the background");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(ChildAnnounced);
        await agent.UpdateAsync(ChildPrompt, "child1");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await Until(() => _log.ToString().Contains("kept open"));

        var stopping = _host.StopTaskAsync("child1", CancellationToken.None);
        var cancel = await agent.ReadAsync("session/cancel");
        Assert.Equal("child1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.True(await stopping);

        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"child1","state":{"state":"idle","stopReason":"cancelled"}}""");
        Assert.Equal(FakeLink.SessionId, await agent.CloseAsync());
        var child = _threads.Get(_store.RowsAfter(_thread.ThreadId, 0).Last(row => row.Kind == "subagent").Payload.GetProperty("threadId").GetString()!)!;
        Assert.Equal("cancelled", _store.RowsAfter(child.ThreadId, 0).Last(row => row.Kind == "turn").Payload.GetProperty("state").GetString());
    }

    private async Task<(FakeLink Agent, string RunId)> StartClaudeBackgroundAsync()
    {
        var runId = _host.Send("start it")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"t1","title":"sleep 15","kind":"execute","status":"pending"}""");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","rawOutput":"Command running in background with ID: b1. Output is being written to: /tmp/b1"}""");
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        var rows = await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() != "started");
        var task = Assert.Single(rows, row => row.Kind == "task");
        Assert.Equal(runId, task.RunId);
        Assert.Equal("""{"name":"Background command","state":"running","canStop":false,"toolCallId":"t1"}""", task.Payload.GetRawText());
        Assert.Equal("finished", rows[^1].Payload.GetProperty("state").GetString());
        await Until(() => _log.ToString().Contains("kept open"));
        return (agent, runId);
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
