using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V1;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Transcript;
using Assembly.Remote.Record;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

/// <summary>Version 1 adapters, replayed from their recordings, read as version 2 at the client.</summary>
public sealed class AcpClientV1Tests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);
    private const string Antigravity = "acp-v1/agy-main.jsonl";
    private const string FirstSession = "5b536aba-06b5-442a-8328-7eb507496cea";
    private const string SecondSession = "b9dbbd11-5d0b-4428-96ac-f1931940b1b4";
    // Antigravity's recorded in-progress edit tool (agy-main.jsonl line 17), on its own.
    private const string RunningEditFile = """
        {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"5b536aba-06b5-442a-8328-7eb507496cea","update":{"toolCallId":"5b536aba-06b5-442a-8328-7eb507496cea:3","title":"Running edit_file","kind":"edit","status":"in_progress","locations":[{"path":"/fixture/home/tsk1694/work/agy/s1/note.txt"}],"rawInput":{"file_path":"/fixture/home/tsk1694/work/agy/s1/note.txt"},"sessionUpdate":"tool_call"}}}
        """;

    private sealed class AskedPolicy : IPermissionPolicy
    {
        private readonly AllowOncePolicy _allow = new();
        public List<RequestPermissionRequest> Asked { get; } = [];

        public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request);
            return _allow.DecideAsync(request, cancellationToken);
        }
    }

    private sealed record Replay(FakeAgent Agent, AcpClient Client, V1Recording Recording, AskedPolicy Policy,
        InitializeResponse Initialized) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private static async Task<Replay> StartAsync(string fixture)
    {
        var agent = new FakeAgent();
        var policy = new AskedPolicy();
        var client = new AcpClient(agent.ClientReads, agent.ClientWrites, policy, TranscriptLog.Discard());
        var recording = new V1Recording(agent, fixture);
        var initialize = client.InitializeAsync(new("fixture", "1"), default);
        await recording.PlayToReplyAsync(1);
        return new Replay(agent, client, recording, policy, await initialize.WaitAsync(Bounded));
    }

    /// <summary>Antigravity with its first session open and its model set, as recorded.</summary>
    private static async Task<Replay> AntigravityAsync()
    {
        var replay = await StartAsync(Antigravity);
        var session = replay.Client.NewSessionAsync(new("/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(2);
        Assert.Equal(FirstSession, (await session.WaitAsync(Bounded)).SessionId);
        var model = replay.Client.SetConfigOptionAsync(FirstSession, "model", "gemini-3.8-flash", default);
        await replay.Recording.PlayToReplyAsync(3);
        await model.WaitAsync(Bounded);
        return replay;
    }

    private static async Task<PromptResult> PromptAsync(Replay replay, string sessionId, long recordedId)
    {
        var run = replay.Client.PromptAsync(sessionId, [TextBlock.Of("recorded")], default);
        await replay.Recording.PlayToReplyAsync(recordedId);
        var result = await run.Ended.WaitAsync(Bounded);
        Assert.Equal(result.MessageId, await run.Inserted.WaitAsync(Bounded));
        return result;
    }

    private static List<SessionNotification> Published(AcpClient client)
    {
        List<SessionNotification> updates = [];
        while (client.Updates.TryRead(out var update)) updates.Add(update);
        return updates;
    }

    [Theory]
    [InlineData(Antigravity, ProtocolShape.Version1)]
    [InlineData("acp-v1/muse-main.jsonl", ProtocolShape.Version1)]
    [InlineData("acp/tools.jsonl", AcpProtocol.Version)]
    [InlineData("acp/codex-tools.jsonl", AcpProtocol.Version)]
    public void The_version_comes_from_the_shape_of_the_initialize_reply(string fixture, int version) =>
        Assert.Equal(version, ProtocolShape.Of(V1Recording.Result(fixture, 1)));

    [Theory]
    [InlineData(Antigravity, "antigravity-acp", "1.4.5", "oauth-personal,oauth-business,agent-platform")]
    [InlineData("acp-v1/muse-main.jsonl", "muse", "1.4.24", "muse-login")]
    public async Task A_recorded_initialize_reply_reads_as_version_2(string fixture, string name, string version, string signIns)
    {
        await using var replay = await StartAsync(fixture);
        var initialized = replay.Initialized;
        Assert.Equal((name, version), (initialized.Info?.Name, initialized.Info?.Version));
        Assert.True(initialized.AcceptsImages);
        Assert.Equal(signIns, string.Join(",", SignInMethods.Runnable(initialized.AuthMethods).Select(method => method.MethodId)));
    }

    [Fact]
    public async Task Muse_initialize_is_accepted_although_it_is_version_1()
    {
        await using var replay = await StartAsync("acp-v1/muse-main.jsonl");
        var session = replay.Client.NewSessionAsync(new("/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(2);
        Assert.Equal("01a1fb07-ac0e-7643-9237-67d62364b5f2", (await session.WaitAsync(Bounded)).SessionId);
    }

    [Fact]
    public async Task The_prompt_reply_ends_the_turn_with_a_made_up_id_and_its_usage()
    {
        await using var replay = await AntigravityAsync();
        var result = await PromptAsync(replay, FirstSession, 4);
        Assert.StartsWith(AcpClient.Version1MessageIdPrefix, result.MessageId);
        Assert.Equal(("end_turn", 14606L), (result.StopReason, result.Usage?.InputTokens));
        Assert.Contains(Published(replay.Client), update => update.Kind == "agent_message_chunk");
    }

    [Fact]
    public async Task The_recorded_approval_names_the_tool_and_its_file_and_is_answered_allow()
    {
        await using var replay = await AntigravityAsync();
        var first = await PromptAsync(replay, FirstSession, 4);
        Published(replay.Client);
        var second = await PromptAsync(replay, FirstSession, 5);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(29693L - 25228L, second.Usage?.InputTokens); // version 1 input counts its cached reads; version 2's does not

        var asked = Assert.Single(replay.Policy.Asked);
        Assert.Equal(("Run create_file?", "fca527de00e344c0820bcb4c2834c906"),
            (asked.Title, asked.ToolCall.GetProperty("toolCallId").GetString()));
        Assert.EndsWith("/note.txt", asked.ToolCall.GetProperty("content")[0].GetProperty("changes")[0].GetProperty("path").GetString());
        Assert.Equal("allow", Assert.Single(replay.Recording.Answers).GetProperty("outcome").GetProperty("optionId").GetString());

        var tools = Published(replay.Client).Where(update => update.Update.TryGetProperty("toolCallId", out _)).ToList();
        Assert.All(tools, tool => Assert.Equal("tool_call_update", tool.Kind));
        Assert.Equal("completed", tools.Last(tool => tool.Update.GetProperty("toolCallId").GetString() == FirstSession + ":3")
            .Update.GetProperty("status").GetString());
        // No stop: the agent's own "failed" for the approved call stays failed.
        Assert.Equal("failed", tools.Last(tool => tool.Update.GetProperty("toolCallId").GetString() == "fca527de00e344c0820bcb4c2834c906")
            .Update.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_recorded_cancel_ends_with_the_reply_and_a_later_cancel_returns_at_once()
    {
        await using var replay = await AntigravityAsync();
        await PromptAsync(replay, FirstSession, 4);
        await PromptAsync(replay, FirstSession, 5);
        var run = replay.Client.PromptAsync(FirstSession, [TextBlock.Of("count")], default);
        await replay.Recording.PlayToClientAsync(AcpMethods.SessionCancel);
        var cancel = replay.Client.CancelAsync(FirstSession, default);
        await replay.Recording.PlayToReplyAsync(6);
        await cancel.WaitAsync(Bounded);
        Assert.Equal("cancelled", (await run.Ended.WaitAsync(Bounded)).StopReason);

        // Nothing is running, so nothing is sent: the next recorded client line is the second session.
        await replay.Client.CancelAsync(FirstSession, default).WaitAsync(Bounded);
        var session = replay.Client.NewSessionAsync(new("/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(7);
        Assert.Equal(SecondSession, (await session.WaitAsync(Bounded)).SessionId);
    }

    [Fact]
    public async Task A_stop_that_crosses_a_natural_end_of_turn_still_returns()
    {
        await using var replay = await AntigravityAsync();
        var run = replay.Client.PromptAsync(FirstSession, [TextBlock.Of("recorded")], default);
        var prompt = await replay.Agent.ReadAsync();
        var cancel = replay.Client.CancelAsync(FirstSession, default);
        Assert.Equal(AcpMethods.SessionCancel, (await replay.Agent.ReadAsync()).GetProperty("method").GetString());
        await replay.Agent.ReplyAsync(prompt, V1Recording.Result(Antigravity, 4).GetRawText());
        await cancel.WaitAsync(Bounded);
        Assert.Equal("end_turn", (await run.Ended.WaitAsync(Bounded)).StopReason);
    }

    // No recording stops mid-tool, so this order is made up: a recorded in-progress tool, then the turn's end.
    [Theory]
    [InlineData("cancelled", "cancelled")]
    [InlineData("end_turn", "failed")]
    public async Task A_tool_still_open_when_the_turn_ends_is_closed(string stopReason, string status)
    {
        await using var replay = await AntigravityAsync();
        var run = replay.Client.PromptAsync(FirstSession, [TextBlock.Of("recorded")], default);
        var prompt = await replay.Agent.ReadAsync();
        await replay.Agent.SendAsync(RunningEditFile.ReplaceLineEndings(""));
        await replay.Agent.ReplyAsync(prompt, $$"""{"stopReason":"{{stopReason}}"}""");
        Assert.Equal(stopReason, (await run.Ended.WaitAsync(Bounded)).StopReason);
        var closed = Published(replay.Client).Last(update => update.Update.TryGetProperty("toolCallId", out var id)
            && id.GetString() == FirstSession + ":3");
        Assert.Equal(("tool_call_update", status), (closed.Kind, closed.Update.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_second_session_on_the_same_process_runs_its_own_turn()
    {
        await using var replay = await AntigravityAsync();
        var first = await PromptAsync(replay, FirstSession, 4);
        await PromptAsync(replay, FirstSession, 5);
        var count = replay.Client.PromptAsync(FirstSession, [TextBlock.Of("count")], default);
        await replay.Recording.PlayToClientAsync(AcpMethods.SessionCancel);
        var cancel = replay.Client.CancelAsync(FirstSession, default);
        await replay.Recording.PlayToReplyAsync(6);
        await cancel.WaitAsync(Bounded);
        await count.Ended.WaitAsync(Bounded);
        var session = replay.Client.NewSessionAsync(new("/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(7);
        await session.WaitAsync(Bounded);

        var second = await PromptAsync(replay, SecondSession, 8);
        Assert.Equal("end_turn", second.StopReason);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(SecondSession, replay.Policy.Asked[^1].SessionId);
        Assert.Equal(2, replay.Recording.Answers.Count);
    }

    private const string MuseSession = "01a1fb07-ac0e-7643-9237-67d62364b5f2";

    /// <summary>Muse with its session resumed, its model set and the prompt's model error played, as recorded.</summary>
    private static async Task<(Replay Replay, PromptRun Run)> MuseModelErrorAsync()
    {
        var replay = await StartAsync("acp-v1/muse-resume.jsonl");
        var resume = replay.Client.ResumeSessionAsync(new(MuseSession, "/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(2);
        await resume.WaitAsync(Bounded);
        var model = replay.Client.SetConfigOptionAsync(MuseSession, "model", "muse-spark-1.3", default);
        await replay.Recording.PlayToReplyAsync(3);
        await model.WaitAsync(Bounded);
        var run = replay.Client.PromptAsync(MuseSession, [TextBlock.Of("recorded")], default);
        await replay.Recording.PlayToReplyAsync(4);
        return (replay, run);
    }

    [Fact]
    public async Task A_model_error_after_only_a_config_update_leaves_the_prompt_not_inserted()
    {
        var (replay, run) = await MuseModelErrorAsync();
        await using var _ = replay;
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => run.Inserted.WaitAsync(Bounded));
        Assert.Equal(JsonRpcException.InternalError, error.Code);
        await Assert.ThrowsAsync<JsonRpcException>(() => run.Ended.WaitAsync(Bounded));
        Assert.Contains(Published(replay.Client), update => update.Kind == "config_option_update");
    }

    [Fact]
    public async Task Close_on_an_adapter_without_the_method_does_not_error()
    {
        var (replay, run) = await MuseModelErrorAsync();
        await using var _ = replay;
        await Assert.ThrowsAsync<JsonRpcException>(() => run.Ended.WaitAsync(Bounded));

        var close = replay.Client.CloseSessionAsync(MuseSession, default);
        await replay.Recording.PlayToReplyAsync(5);
        await close.WaitAsync(Bounded);
    }

    [Fact]
    public async Task A_resumed_session_runs_a_turn_and_closes()
    {
        const string session = FirstSession;
        await using var replay = await StartAsync("acp-v1/agy-resume.jsonl");
        var resume = replay.Client.ResumeSessionAsync(new(session, "/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(2);
        await resume.WaitAsync(Bounded);

        var result = await PromptAsync(replay, session, 3);
        Assert.Equal(("end_turn", 15163L - 13117L), (result.StopReason, result.Usage?.InputTokens));
        Assert.Contains(Published(replay.Client), update => update.Kind == "agent_message_chunk");

        var close = replay.Client.CloseSessionAsync(session, default);
        await replay.Recording.PlayToReplyAsync(4);
        await close.WaitAsync(Bounded);
    }

    [Fact]
    public async Task Version_1_login_sends_authenticate()
    {
        await using var replay = await StartAsync(Antigravity);
        var login = replay.Client.LoginAsync("oauth-personal", _ => { }, default);
        var request = await replay.Agent.ReadAsync(AcpMethods.Authenticate);
        Assert.Equal(AcpMethods.Authenticate, request.GetProperty("method").GetString());
        Assert.Equal("oauth-personal", request.GetProperty("params").GetProperty("methodId").GetString());
        // Made-up reply: the recordings were signed in. The spec's authenticate reply is an empty object.
        await replay.Agent.ReplyAsync(request, "{}");
        await login.WaitAsync(Bounded);
    }

    [Fact]
    public async Task Version_1_logout_is_sent_when_advertised()
    {
        await using var replay = await StartAsync(Antigravity);
        Assert.True(replay.Initialized.CanLogout);
        var logout = replay.Client.LogoutAsync(default);
        var request = await replay.Agent.ReadAsync(AcpMethods.Logout);
        Assert.Equal(AcpMethods.Logout, request.GetProperty("method").GetString());
        await replay.Agent.ReplyAsync(request, "{}");
        await logout.WaitAsync(Bounded);
    }

    [Fact]
    public async Task Version_1_logout_sends_nothing_when_not_advertised()
    {
        await using var replay = await StartAsync("acp-v1/muse-main.jsonl");
        Assert.False(replay.Initialized.CanLogout);
        await replay.Client.LogoutAsync(default).WaitAsync(Bounded);
        // The client's next line must be the recorded session/new, so nothing was sent for the logout.
        var session = replay.Client.NewSessionAsync(new("/fixture", []), default);
        await replay.Recording.PlayToReplyAsync(2);
        await session.WaitAsync(Bounded);
    }

    [Theory]
    [InlineData("agent_message_chunk", true)]
    [InlineData("agent_thought_chunk", true)]
    [InlineData("tool_call", true)]
    [InlineData("tool_call_update", true)]
    [InlineData("config_option_update", false)]
    [InlineData("usage_update", false)]
    [InlineData("available_commands_update", false)]
    [InlineData("current_mode_update", false)]
    public void Only_turn_content_is_a_sign_of_life(string kind, bool counts) => Assert.Equal(counts, SignOfLife.Counts(kind));
}
