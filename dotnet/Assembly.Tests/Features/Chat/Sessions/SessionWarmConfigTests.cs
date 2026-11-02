using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Common.Acp;
using Assembly.Application.Features.Chat.Sessions.Configuration;
using Assembly.Infrastructure.Transcript;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionWarmConfigTests : IAsyncDisposable
{
    private readonly TempDatabase _temp = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "a43-warm-" + Guid.NewGuid().ToString("N"));
    private readonly BackgroundWork _work;
    private readonly AppDatabase _db;
    private readonly LiveHub _hub;
    private readonly SessionStore _store;
    private readonly FakeLauncher _launcher = new();
    private readonly ObservedTime _time = new();
    private readonly SharedAdapter _pool;
    private readonly SessionHost _host;
    private readonly SessionInfo _session;
    private readonly SessionOpener _opener;
    private string Target => ConfigTargetIdentity.Capture(_launcher, "claude").TargetId;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    internal const string Options = """[{"id":"model","type":"select","category":"model","currentValue":"b","options":[{"value":"a","name":"A"},{"value":"b","name":"B"}]}]""";

    public SessionWarmConfigTests()
    {
        Directory.CreateDirectory(_folder);
        _db = TestDatabase.Open(_temp.Path);
        _hub = new LiveHub(RowStore.Open(_db));
        _store = SessionStore.Open(_db, _time, "/work");
        _session = _store.Create("claude", "repo", workingFolder: _folder);
        _pool = new SharedAdapter(_launcher, "claude", _time, TextWriter.Null);
        _opener = new SessionOpener(_store, _launcher, _ => _pool);
        _work = new BackgroundWork(_time, TextWriter.Null);
        _host = new SessionHost(_session.SessionId, _hub, _store, _opener, _time, TextWriter.Null, _work);
    }

    [Fact]
    public async Task Nonnull_agent_fallback_does_not_suppress_Bs_own_native_id_and_cwd_warm()
    {
        _store.SetAgentConfig("claude", "[]");
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var before = _store.Get(_session.SessionId)!;
        var warm = _host.WarmConfigAsync(Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync(acpSessionId: "saved-B", configOptions: Options);
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        Assert.Equal("saved-B", open.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(_folder, open.GetProperty("params").GetProperty("cwd").GetString());
        var result = await warm.WaitAsync(Wait);
        Assert.Equal(Options, result.Options);
        Assert.Equal(before, _store.Get(_session.SessionId));
        Assert.True(agent.Disposed);
        Assert.Empty(_launcher.CheckedOut);
        Assert.Null(_host.ActiveRunId);
        // No native session/close, rename or prompt was received: disposal ended this private read.
    }

    [Fact]
    public async Task Unsupported_resume_refuses_without_new_session_or_native_id_change()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var warm = _host.WarmConfigAsync(Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await Initialize(agent);
        await agent.ReplyErrorAsync(await agent.ReadAsync("session/resume"), -32601, "unsupported resume fixture");
        await Assert.ThrowsAsync<CallFailed>(() => warm.WaitAsync(Wait));
        Assert.Equal("saved-B", _store.Get(_session.SessionId)!.AcpSessionId);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Resume_refusal_does_not_fallback_or_close()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var warm = _host.WarmConfigAsync(Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await Initialize(agent);
        var resume = await agent.ReadAsync("session/resume");
        await agent.ReplyErrorAsync(resume, -32602, "lost transcript");
        await Assert.ThrowsAnyAsync<Exception>(() => warm.WaitAsync(Wait));
        Assert.Equal("saved-B", _store.Get(_session.SessionId)!.AcpSessionId);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Idle_pick_saves_only_accepted_model_and_full_actual_options()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var picking = _host.SetAndWarmConfigAsync("model", "a", Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options);
        var set = await agent.ReadAsync("session/set_config_option");
        Assert.Empty(_store.Config(_session.SessionId).Choices);
        var changed = Options.Replace("\"currentValue\":\"b\"", "\"currentValue\":\"a\"");
        await agent.ReplyAsync(set, $$"""{"configOptions":{{changed}}}""");
        var result = await picking.WaitAsync(Wait);
        Assert.Equal("a", result.Choices["model"]);
        Assert.Equal(changed, result.Options);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Refused_idle_pick_preserves_old_options_and_choices()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        _store.SetConfigChoice(_session.SessionId, "model", "b");
        _store.SetConfigOptions(_session.SessionId, Options);
        var picking = _host.SetAndWarmConfigAsync("model", "a", Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options);
        var set = await agent.ReadAsync("session/set_config_option");
        await agent.ReplyErrorAsync(set, -32602, "refused");
        await Assert.ThrowsAnyAsync<Exception>(() => picking.WaitAsync(Wait));
        Assert.Equal("b", _store.Config(_session.SessionId).Choices["model"]);
        Assert.Equal(Options, _store.Config(_session.SessionId).Options);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Cancelled_late_resume_is_forgotten_without_close_or_observation()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        using var cancel = new CancellationTokenSource();
        var warm = _host.WarmConfigAsync(Target, cancel.Token);
        var agent = await _launcher.NextAsync();
        await Initialize(agent);
        var resume = await agent.ReadAsync("session/resume");
        cancel.Cancel();
        await agent.ReplyAsync(resume, $$"""{"configOptions":{{Options}}}""");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warm.WaitAsync(Wait));
        await _host.StopWarmForShutdownAsync();
        Assert.Null(_store.Config(_session.SessionId).Options);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task A_target_replaced_while_resuming_cannot_commit_its_result()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var warm = _host.WarmConfigAsync(Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await Initialize(agent);
        var resume = await agent.ReadAsync("session/resume");
        _launcher.Agents = [_launcher.Agents[0] with { Args = ["replacement"] }];
        await agent.ReplyAsync(resume, $$"""{"configOptions":{{Options}}}""");
        await Assert.ThrowsAsync<CallFailed>(() => warm.WaitAsync(Wait));
        Assert.Null(_store.Config(_session.SessionId).Options);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Observed_null_commands_and_options_do_not_install_another_sessions_fallback()
    {
        var a = _store.Create("claude", "other");
        _store.SetCommands(a.SessionId, """[{"name":"from-A"}]""");
        _store.SetAgentConfig("claude", Options);
        _store.SetAcpSession(_session.SessionId, "saved-B");
        Assert.Contains("from-A", _store.Config(_session.SessionId).Commands);
        var warming = _host.WarmConfigAsync(Target, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var result = await warming.WaitAsync(Wait);
        var wire = ComposerConfigJson.Json(result);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("options").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("commands").ValueKind);
        Assert.Equal(JsonValueKind.Null, ComposerConfigJson.Json(_store.Config(_session.SessionId)).GetProperty("commands").ValueKind);
        Assert.Equal(1, _launcher.Launches);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task A_real_send_supersedes_warm_and_waits_for_its_exact_process_cleanup_before_resume()
    {
        _store.SetAcpSession(_session.SessionId, "saved-B");
        var warming = _host.WarmConfigAsync(Target, CancellationToken.None);
        var old = await _launcher.NextAsync();
        await Initialize(old);
        var resume = await old.ReadAsync("session/resume");
        var sending = _host.SendAsync("real message", null, null, CancellationToken.None);
        Assert.NotNull(_host.CurrentTurn.RunId);
        await old.ReplyAsync(resume, "{\"configOptions\":" + Options + "}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming.WaitAsync(Wait));
        var real = await _launcher.NextAsync();
        Assert.True(old.Disposed);
        var opened = await real.StartAsync(acpSessionId: "saved-B", configOptions: Options);
        Assert.Equal("saved-B", opened.GetProperty("params").GetProperty("sessionId").GetString());
        var prompt = await real.ReadAsync("session/prompt");
        Assert.Equal("real message", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await real.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.IsType<SendResult.Inserted>(await sending.WaitAsync(Wait));
        await real.CloseAsync();
    }

    [Fact]
    public async Task Never_answering_cancelled_warm_retires_only_its_process_and_fences_native_id_until_other_borrower_exits()
    {
        var borrowing = _pool.GetAsync(new StageTimes(_time), CancellationToken.None);
        var old = await _launcher.NextAsync();
        await Initialize(old);
        var other = await borrowing;
        _store.SetAcpSession(_session.SessionId, "saved-B");
        using var cancel = new CancellationTokenSource();
        var warming = _host.WarmConfigAsync(Target, cancel.Token);
        await old.ReadAsync("session/resume");
        cancel.Cancel();
        await _time.CloseTimerCreated.Task.WaitAsync(Wait);
        _time.Advance(SharedAdapter.CloseLimit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming.WaitAsync(Wait));
        Assert.False(old.Disposed);
        Assert.Null(_store.Config(_session.SessionId).Options);
        var replacement = _pool.GetForSessionAsync("saved-B", new StageTimes(_time), CancellationToken.None);
        Assert.False(replacement.IsCompleted);
        Assert.Equal(1, _launcher.Launches);
        await _pool.ReleaseAsync(other);
        Assert.True(old.Exited.IsCompleted);
        var next = await _launcher.NextAsync();
        await Initialize(next);
        var borrowed = await replacement.WaitAsync(Wait);
        await _pool.ReleaseWarmAsync(borrowed, "saved-B");
        Assert.True(next.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_ACP_process_is_stopped_after_metadata_read_without_prompt_or_chat_rows(bool draft)
    {
        var marker = Path.Combine(Path.GetTempPath(), "a39-no-prompt-" + Guid.NewGuid().ToString("N"));
        AgentConnection? adapter = null;
        _launcher.Launch = _ => Task.FromResult<IAgentLink>(adapter = AgentConnection.Start(
            new AgentCommand("/usr/bin/perl", ["-e", FakeAdapter, marker], "/tmp"), new UnclaimedPermissions(), TranscriptLog.Discard()));
        using var live = _hub.Subscribe(_session.SessionId, 0);
        if (draft) await new ComposerConfigDiscovery(_launcher, TimeProvider.System).DiscoverAsync("claude", Target, "/tmp", CancellationToken.None);
        else await _host.WarmConfigAsync(Target, CancellationToken.None);
        await adapter!.Exited.WaitAsync(Wait);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(adapter.ProcessId));
        Assert.False(File.Exists(marker));
        Assert.False(live.Rows.TryRead(out _));
        Assert.Null(_host.ActiveRunId);
    }

    [Fact]
    public async Task A_late_live_pick_cannot_overwrite_a_new_real_turns_generation()
    {
        _store.SetConfigChoice(_session.SessionId, "model", "b");
        var firstSend = _host.SendAsync("first", null, null, default);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options);
        var firstPrompt = await agent.ReadAsync("session/prompt");
        var firstTurn = _host.CurrentTurn;
        var picking = _host.SetConfigAsync("model", "a", default, Target);
        var set = await agent.ReadAsync("session/set_config_option");
        await agent.FinishAsync(firstPrompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await firstTurn.Done.WaitAsync(Wait);
        Assert.IsType<SendResult.Inserted>(await firstSend.WaitAsync(Wait));
        var secondSend = _host.SendAsync("second", null, null, default);
        await agent.OpenAsync(configOptions: Options);
        var secondPrompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyAsync(set, "{\"configOptions\":" + Options.Replace("\"currentValue\":\"b\"", "\"currentValue\":\"a\"") + "}");
        await Assert.ThrowsAsync<CallFailed>(() => picking.WaitAsync(Wait));
        Assert.Equal("b", _store.Config(_session.SessionId).Choices["model"]);
        Assert.Equal(Options, _store.Config(_session.SessionId).Options);
        await agent.FinishAsync(secondPrompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        Assert.IsType<SendResult.Inserted>(await secondSend.WaitAsync(Wait));
    }

    private const string FakeAdapter = """
        $| = 1;
        while (<STDIN>) {
            my ($id) = /"id":(\d+)/;
            print qq({"jsonrpc":"2.0","id":$id,"result":{"protocolVersion":2,"capabilities":{},"authMethods":[]}}\n) if /"method":"initialize"/;
            print qq({"jsonrpc":"2.0","id":$id,"result":{"sessionId":"acp-1"}}\n) if /"method":"session\/new"/;
            if (/"method":"session\/prompt"/) { open(my $f, ">", $ARGV[0]); close($f); }
        }
        """;

    private sealed class ObservedTime : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        internal TaskCompletionSource CloseTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == SharedAdapter.CloseLimit) CloseTimerCreated.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan duration) => _inner.Advance(duration);
    }

    internal static async Task Initialize(FakeLink agent)
    {
        var init = await agent.ReadAsync("initialize");
        await agent.ReplyAsync(init, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
    }

    public async ValueTask DisposeAsync()
    {
        var warm = _host.StopWarmForShutdownAsync();
        var turn = _host.StopForShutdownAsync();
        await _pool.DisposeAsync();
        await Task.WhenAll(warm, turn);
        await _work.DisposeAsync();
        _hub.Dispose(); _db.Dispose(); _temp.Dispose();
        Directory.Delete(_folder);
    }
}
