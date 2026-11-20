using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

// A message sent while a turn runs, on an agent that queues prompts (Claude) or takes the steering call (Codex).
public sealed partial class SessionHostTests
{
    private const string Running = """{"sessionUpdate":"state_update","state":"running"}""";
    private const string Idle = """{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""";
    private const string OpenTool = """{"sessionUpdate":"tool_call_update","toolCallId":"probe","name":"exec_command","status":"in_progress"}""";

    private static string UserMessage(string id) =>
        JsonSerializer.Serialize(new { sessionUpdate = "user_message", messageId = id, content = Array.Empty<object>() });

    // A turn on an agent that queues prompts: its prompt is acknowledged and running, and inserted unless its reply is withheld.
    private async Task<(FakeLink Agent, string Run, JsonElement Prompt)> QueueingTurnAsync(SessionHost host, bool withholdReply = false)
    {
        var run = host.Send("one")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(canSteer: false, promptQueueing: true);
        var prompt = await agent.ReadAsync("session/prompt");
        if (withholdReply)
        {
            await agent.UpdateAsync(UserMessage("a"));
            await agent.UpdateAsync(Running);
        }
        else await agent.InsertAsync(prompt, "a");
        return (agent, run, prompt);
    }

    private static async Task<(Task<SteerResult> Result, JsonElement Request)> Steer(SessionHost host, FakeLink agent, string text, string clientId)
    {
        var result = host.SteerAsync(text, clientId, null, CancellationToken.None);
        return (result, await agent.ReadAsync("session/prompt"));
    }

    private List<StoredRow> Stored() => _store.RowsAfter(_session.SessionId, 0).ToList();

    private List<string> UserTexts() =>
        Stored().Where(row => row.Kind == "user").Select(row => row.Payload.GetProperty("text").GetString()!).ToList();

    private static bool Finished(StoredRow row) => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "finished";

    // The wait ended without an outcome: not a refusal, not a delivery, and not this test timing out.
    private static async Task Uncertain(Task<SteerResult> steer)
    {
        var error = await Record.ExceptionAsync(() => steer.WaitAsync(Wait));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
    }

    // Records the engine session's state after each write the feed takes part in, and can fail chosen batches.
    internal class StateTrace : DispatchProxy
    {
        public ISessionFeed Inner = null!;
        public string SessionId = "";
        public readonly List<string> States = [];
        public Func<IReadOnlyList<AgentEvent>, bool>? FailBatch;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionFeed.ObserveBatch) && FailBatch is { } fail && fail((IReadOnlyList<AgentEvent>)args![3]!))
                throw new InvalidOperationException("The batch's write failed.");
            object? result = null;
            try { result = method.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); }
            if (args?.OfType<IWriteScope>().FirstOrDefault() is { } scope
                && scope.QueryFirst("SELECT state FROM engine_session WHERE id=$id", row => row.Text(0), new SqlArg("$id", SessionId)) is { } state)
                lock (States)
                    if (States.Count == 0 || States[^1] != state) States.Add(state);
            return result;
        }
    }

    private StateTrace Trace(ISessionFeed inner)
    {
        var trace = (StateTrace)(object)DispatchProxy.Create<ISessionFeed, StateTrace>();
        trace.Inner = inner;
        trace.SessionId = _session.SessionId;
        return trace;
    }

    private EngineRuntime Engine() => EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
        cutover: new(_database, _sessionStore, _launcher, _db.Folder, _time));

    private string? EngineState() =>
        _database.QueryFirst("SELECT state FROM engine_session WHERE id=$id", row => row.Text(0), default, new SqlArg("$id", _session.SessionId));

    [Fact]
    public async Task Folded_steers_land_after_the_tool_in_order_in_the_same_run()
    {
        var (agent, run, _) = await QueueingTurnAsync(_host);
        await agent.UpdateAsync(OpenTool);
        await RowsUntil(row => row.Kind == "tool");
        var (first, firstRequest) = await Steer(_host, agent, "also one", "c1");
        var (second, secondRequest) = await Steer(_host, agent, "also two", "c2");
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"probe","status":"completed"}""");
        await agent.UpdateAsync(UserMessage("b1"));
        await agent.ReplyAsync(firstRequest, """{"messageId":"b1"}""");
        Assert.Equal(SteerResult.Injected, await first.WaitAsync(Wait));
        Assert.Equal(["one", "also one"], UserTexts());
        await agent.ReplyAsync(secondRequest, """{"messageId":"b2"}""");
        await agent.UpdateAsync(UserMessage("b2"));
        Assert.Equal(SteerResult.Injected, await second.WaitAsync(Wait));
        Assert.Equal(["one", "also one", "also two"], UserTexts());
        await agent.TextAsync("Both done", "m2");
        await agent.UpdateAsync(Idle);
        await RowsUntil(Finished);
        await agent.CloseAsync();

        var rows = Stored().Where(row => row.Kind is "tool" or "user" or "message" or "turn").ToList();
        Assert.Equal(["user", "turn", "tool", "tool", "user", "user", "message", "turn"], rows.Select(row => row.Kind));
        Assert.All(rows, row => Assert.Equal(run, row.RunId));
        Assert.Single(rows, Finished);
    }

    [Theory]
    [InlineData("echo,reply,running", "end_turn")]
    [InlineData("running,echo,reply", "end_turn")]
    [InlineData("reply,echo,running", "end_turn")]
    [InlineData("reply,echo,running", "reasonless")]
    [InlineData("echo,reply,running", "cancelled")]
    [InlineData("running,echo,reply", "repeated")]
    public async Task Events_after_a_prompted_turns_end_belong_to_the_next_run_whatever_their_order(string order, string end)
    {
        await using var runtime = Engine();
        var trace = Trace(new EngineFeed(runtime, _log, _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: (ISessionFeed)(object)trace);
        var host = registry.For(_session.SessionId);
        // The barrier: A's reply is withheld, so A cannot finish before everything of B's has been sent.
        var (agent, a, prompt) = await QueueingTurnAsync(host, withholdReply: true);
        var (steer, request) = await Steer(host, agent, "also this", "c1");
        await agent.UpdateAsync(end switch
        {
            "reasonless" => """{"sessionUpdate":"state_update","state":"idle"}""",
            "cancelled" => """{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""",
            _ => Idle
        });
        if (end == "repeated") await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle"}""");
        foreach (var step in order.Split(','))
            await (step switch
            {
                "echo" => agent.UpdateAsync(UserMessage("b")),
                "reply" => agent.ReplyAsync(request, """{"messageId":"b"}"""),
                _ => agent.UpdateAsync(Running)
            });
        await agent.TextAsync("Answer to it", "m-b");
        await agent.UpdateAsync(Idle);
        var closing = agent.NextMessageAsync();
        await agent.ReplyAsync(prompt, """{"messageId":"a"}""");

        Assert.Equal(SteerResult.Injected, await steer.WaitAsync(Wait));
        Assert.Contains("also this", UserTexts());
        var close = await closing.WaitAsync(Wait);
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await agent.ReplyAsync(close, "{}");
        await Until(() => !host.IsOpen);

        var rows = Stored().Where(row => row.Kind is "user" or "message" or "turn").ToList();
        var b = rows.Single(row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == "also this").RunId;
        Assert.NotEqual(a, b);
        Assert.Equal(["user", "turn", "turn"], rows.Where(row => row.RunId == a).Select(row => row.Kind));
        Assert.Equal(["turn", "user", "message", "turn"], rows.Where(row => row.RunId == b).Select(row => row.Kind));
        Assert.False(rows.First(row => row.RunId == b).Payload.TryGetProperty("reason", out _));
        Assert.Equal(1, _launcher.Launches);
        lock (trace.States)
            Assert.Equal(["running", "idle", "running", "idle"], trace.States.SkipWhile(state => state != "running"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Steer_admission_and_the_keep_decision_share_one_lock(bool beforeTheEnd)
    {
        var (agent, _, prompt) = await QueueingTurnAsync(_host, withholdReply: true);
        await agent.UpdateAsync(Idle);
        if (!beforeTheEnd)
        {
            await agent.ReplyAsync(prompt, """{"messageId":"a"}""");
            await Until(() => _host.ActiveRunId is null);
            Assert.False(_host.IsOpen);
            Assert.Equal(SteerResult.NotRunning, await _host.SteerAsync("late", "c1", null, CancellationToken.None).WaitAsync(Wait));
            await agent.CloseAsync();
            Assert.DoesNotContain("late", UserTexts());
            return;
        }
        var (steer, request) = await Steer(_host, agent, "in time", "c1");
        await agent.ReplyAsync(prompt, """{"messageId":"a"}""");
        await Until(() => _host.ActiveRunId is null);
        Assert.True(_host.IsOpen);
        var next = agent.NextMessageAsync();
        Assert.False(next.IsCompleted);
        await agent.ReplyErrorAsync(request, -32800, "Request cancelled");
        Assert.Equal(SteerResult.PromptRequired, await steer.WaitAsync(Wait));
        var close = await next.WaitAsync(Wait);
        Assert.Equal("session/close", close.GetProperty("method").GetString());
        await agent.ReplyAsync(close, "{}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Steer_during_a_self_started_run_joins_it_or_starts_the_next(bool beforeItsIdle)
    {
        var (agent, a, _) = await QueueingTurnAsync(_host);
        await agent.UpdateAsync(Background);
        await agent.UpdateAsync(Idle);
        await Until(() => _host.ActiveRunId is null);
        await agent.UpdateAsync(Running);
        await Until(() => _host.ActiveRunId is not null);
        var self = _host.ActiveRunId!;
        var (steer, request) = await Steer(_host, agent, "also this", "c1");
        if (!beforeItsIdle)
        {
            await agent.UpdateAsync(Idle);
            await Until(() => _host.ActiveRunId is null);
        }
        await agent.UpdateAsync(UserMessage("b"));
        await agent.ReplyAsync(request, """{"messageId":"b"}""");
        Assert.Equal(SteerResult.Injected, await steer.WaitAsync(Wait));
        if (!beforeItsIdle)
        {
            await agent.UpdateAsync(Running);
            await agent.TextAsync("Answer", "m-b");
        }
        await agent.UpdateAsync(Idle);
        await Until(() => _host.ActiveRunId is null);

        var user = Stored().Single(row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == "also this");
        if (beforeItsIdle) Assert.Equal(self, user.RunId);
        else
        {
            Assert.NotEqual(self, user.RunId);
            Assert.NotEqual(a, user.RunId);
            Assert.False(Stored().First(row => row.RunId == user.RunId).Payload.TryGetProperty("reason", out _));
        }
        await agent.UpdateAsync(NoBackground);
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Adapter_lost_under_a_message_run_fails_that_run_only()
    {
        await using var runtime = Engine();
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(_session.SessionId);
        var (agent, a, prompt) = await QueueingTurnAsync(host, withholdReply: true);
        var (steer, request) = await Steer(host, agent, "also this", "c1");
        await agent.UpdateAsync(Idle);
        await agent.ReplyAsync(prompt, """{"messageId":"a"}""");
        await agent.UpdateAsync(UserMessage("b"));
        await agent.ReplyAsync(request, """{"messageId":"b"}""");
        Assert.Equal(SteerResult.Injected, await steer.WaitAsync(Wait));
        await agent.UpdateAsync(Running);
        await agent.TextAsync("Working on it", "m-b");
        await RowsUntil(row => row.Kind == "delta");
        agent.Crash();
        var lost = (await RowsUntil(row => row.Kind == "turn" && row.Payload.GetProperty("state").GetString() == "failed"))[^1];

        var b = Stored().Single(row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == "also this").RunId;
        Assert.NotEqual(a, b);
        Assert.Equal(b, lost.RunId);
        Assert.True(Finished(Stored().Last(row => row.RunId == a && row.Kind == "turn")));
        await Until(() => EngineState() == "lost");
    }

    [Theory]
    [InlineData("pipe closes before the reader takes it")]
    [InlineData("its row's write fails")]
    public async Task An_inserted_steer_that_is_not_recorded_is_uncertain(string how)
    {
        await using var runtime = Engine();
        var trace = Trace(new EngineFeed(runtime, _log, _time));
        trace.FailBatch = events => events.Any(e => e is SteerInserted);
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work, feed: (ISessionFeed)(object)trace);
        var host = registry.For(_session.SessionId);
        var (agent, _, prompt) = await QueueingTurnAsync(host, withholdReply: true);
        var (steer, request) = await Steer(host, agent, "also this", "c1");
        // The reader stops at A's end until A's reply comes, so B's insertion waits behind it.
        await agent.UpdateAsync(Idle);
        if (how.StartsWith("pipe", StringComparison.Ordinal))
        {
            await agent.UpdateAsync(UserMessage("b"));
            await agent.ReplyAsync(request, """{"messageId":"b"}""");
            agent.Crash();
        }
        else
        {
            await agent.ReplyAsync(prompt, """{"messageId":"a"}""");
            await agent.UpdateAsync(UserMessage("b"));
            await agent.ReplyAsync(request, """{"messageId":"b"}""");
        }
        await Uncertain(steer);
        Assert.DoesNotContain("also this", UserTexts());
        await Until(() => !host.IsOpen);
    }

    [Theory]
    [InlineData("prompted", true)]
    [InlineData("prompted", false)]
    [InlineData("self-started", true)]
    [InlineData("self-started", false)]
    public async Task A_kept_session_bounds_a_steer_that_never_settles(string origin, bool answersTheCancel)
    {
        var (agent, _, _) = await QueueingTurnAsync(_host);
        // A second session on the same adapter keeps it alive, so only the bound settles the steer.
        var other = Other();
        Assert.NotNull(other.Send("busy elsewhere"));
        await agent.OpenAsync("acp-2");
        await agent.ReadAsync("session/prompt");
        Task<SteerResult> steer;
        JsonElement request;
        if (origin == "prompted")
        {
            (steer, request) = await Steer(_host, agent, "also this", "c1");
            await agent.UpdateAsync(Idle);
        }
        else
        {
            await agent.UpdateAsync(Background);
            await agent.UpdateAsync(Idle);
            await Until(() => _host.ActiveRunId is null);
            await agent.UpdateAsync(Running);
            await Until(() => _host.ActiveRunId is not null);
            (steer, request) = await Steer(_host, agent, "also this", "c1");
            await agent.UpdateAsync(Idle);
        }
        await Until(() => _host.ActiveRunId is null);
        Assert.True(_host.IsOpen);

        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.QuietMs));
        var cancel = await agent.ReadAsync("session/cancel");
        Assert.Equal(FakeLink.AcpSessionId, cancel.GetProperty("params").GetProperty("sessionId").GetString());
        if (answersTheCancel)
        {
            await agent.ReplyErrorAsync(request, -32800, "Request cancelled");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""");
            Assert.Equal(SteerResult.PromptRequired, await steer.WaitAsync(Wait));
            if (origin != "prompted") await agent.UpdateAsync(NoBackground);
            Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
            Assert.DoesNotContain("also this", UserTexts());
            return;
        }
        // The agent ignores the cancel and goes on with a tool that stays open.
        await agent.UpdateAsync(Running);
        await agent.UpdateAsync(OpenTool);
        await RowsUntil(row => row.Kind == "tool");
        _time.Advance(TimeSpan.FromMilliseconds(SessionHost.QuietMs));
        var close = await agent.ReadAsync("session/close");
        Assert.Equal(FakeLink.AcpSessionId, close.GetProperty("params").GetProperty("sessionId").GetString());
        await Uncertain(steer);
        Assert.False(_host.IsOpen);
        await Until(() =>
        {
            _time.Advance(SharedAdapter.CloseLimit);
            return _log.ToString().Contains("kept session closed");
        });
        Assert.DoesNotContain("also this", UserTexts());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_waiting_steer_is_settled_when_the_session_closes_or_the_adapter_ends(bool closeAnswered)
    {
        var (agent, _, _) = await QueueingTurnAsync(_host);
        var (steer, request) = await Steer(_host, agent, "also this", "c1");
        await agent.UpdateAsync(Idle);
        await Until(() => _host.ActiveRunId is null);
        if (closeAnswered)
        {
            var closing = _host.CloseKeptForShutdownAsync();
            var close = await agent.ReadAsync("session/close");
            await agent.ReplyErrorAsync(request, -32800, "Request cancelled");
            Assert.Equal(SteerResult.PromptRequired, await steer.WaitAsync(Wait));
            await agent.ReplyAsync(close, "{}");
            await closing.WaitAsync(Wait);
        }
        else
        {
            var stopping = _host.StopForShutdownAsync();
            await _adapter.DisposeAsync();
            await stopping.WaitAsync(Wait);
            await Uncertain(steer);
        }
        Assert.False(_host.IsOpen);
        Assert.DoesNotContain("also this", UserTexts());
    }

    [Fact]
    public async Task Codex_injection_followed_at_once_by_idle_lands_in_that_turn()
    {
        var run = _host.Send("one")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "a");
        var steering = _host.SteerAsync("also this", "c1", null, CancellationToken.None);
        var steer = await agent.ReadAsync("_session/steering");
        await agent.ReplyThenUpdateAsync(steer, """{"outcome":"injected"}""", Idle);
        Assert.Equal(SteerResult.Injected, await steering.WaitAsync(Wait));
        await RowsUntil(Finished);
        await agent.CloseAsync();

        var rows = Stored();
        var user = rows.Single(row => row.Kind == "user" && row.Payload.GetProperty("text").GetString() == "also this");
        Assert.Equal(run, user.RunId);
        Assert.True(user.Seq < rows.Single(Finished).Seq);
        Assert.Equal("steer_injected", user.Payload.GetProperty("acknowledgment").GetProperty("kind").GetString());
    }
}
