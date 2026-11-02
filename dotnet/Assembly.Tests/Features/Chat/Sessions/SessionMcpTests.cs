using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Mcp;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionMcpTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AppDatabase _db;
    private readonly SessionStore _store;
    private readonly FakeLauncher _launcher = new();
    private readonly SharedAdapter _adapter;
    private readonly string _id;
    private readonly McpTokens _tokens = new() { Url = "http://localhost:12345/mcp" };
    private readonly SessionObservation _observed;

    public SessionMcpTests()
    {
        var time = _time;
        _db = TestDatabase.Open(_file.Path);
        RowStore.Open(_db);
        _store = SessionStore.Open(_db, time, "checkouts");
        _id = _store.Create("claude", "https://github.com/o/r").SessionId;
        _adapter = new(_launcher, "claude", time, TextWriter.Null);
        _observed = new(_id, null, null, null, 9);
    }

    [Fact]
    public async Task Cancel_before_ready_never_checks_out_or_acquires_adapter()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opener = Opener(ready.Task);
        using var cancel = new CancellationTokenSource();
        var opening = opener.OpenAsync(_id, "first", new(TimeProvider.System), cancel.Token, _observed);
        Assert.Empty(_launcher.CheckedOut);
        Assert.Equal(0, _launcher.Launches);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Empty(_launcher.CheckedOut);
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Stale_after_slow_config_closes_exact_open_revokes_and_releases_before_prompt()
    {
        _store.SetConfigChoice(_id, "mode", "plan");
        var stale = false;
        var opener = Opener(validate: () => { if (stale) throw new CallFailed("stale_epoch"); });
        var opening = opener.OpenAsync(_id, "first", new(TimeProvider.System), default, _observed);
        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync(configOptions: Options);
        var token = Token(open);
        Assert.Equal(new McpCaller(_id, 9), _tokens.Caller(token));
        var config = await agent.ReadAsync("session/set_config_option");
        stale = true;
        await agent.ReplyAsync(config, """{"configOptions":[]}""");
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        Assert.Equal("stale_epoch", (await Assert.ThrowsAsync<CallFailed>(() => opening)).Message);
        Assert.Null(_tokens.Caller(token));
        _time.Advance(SharedAdapter.IdleGrace);
        await agent.Exited.WaitAsync(Wait);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Cancelled_open_closes_late_answer_and_releases_exact_adapter()
    {
        using var cancel = new CancellationTokenSource();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = Opener(onRevoke: () => revoked.TrySetResult()).OpenAsync(_id, "first", new(TimeProvider.System), cancel.Token, _observed);
        var agent = await _launcher.NextAsync();
        var init = await agent.ReadAsync("initialize");
        await agent.ReplyAsync(init, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
        var open = await agent.ReadAsync("session/new");
        var token = Token(open);
        cancel.Cancel();
        await revoked.Task.WaitAsync(Wait);
        Assert.Null(_tokens.Caller(token));
        await agent.ReplyAsync(open, """{"sessionId":"late"}""");
        Assert.Equal("late", await agent.CloseAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Null(_tokens.Caller(token));
        _time.Advance(SharedAdapter.IdleGrace);
        await agent.Exited.WaitAsync(Wait);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Resume_refusal_revokes_old_token_before_new_attempt_and_success_is_not_revoked_by_old_cleanup()
    {
        _store.SetAcpSession(_id, "saved");
        var opening = Opener().OpenAsync(_id, "first", new(TimeProvider.System), default, _observed);
        var agent = await _launcher.NextAsync();
        var init = await agent.ReadAsync("initialize");
        await agent.ReplyAsync(init, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
        var resume = await agent.ReadAsync("session/resume");
        Assert.Equal("saved", resume.GetProperty("params").GetProperty("sessionId").GetString());
        var oldToken = Token(resume);
        await agent.ReplyErrorAsync(resume, -32602, "transcript gone");
        var fresh = await agent.ReadAsync("session/new");
        var token = Token(fresh);
        Assert.Null(_tokens.Caller(oldToken));
        Assert.NotEqual(oldToken, token);
        await agent.ReplyAsync(fresh, """{"sessionId":"replacement"}""");
        var session = await opening.WaitAsync(Wait);
        Assert.Equal("replacement", _store.Get(_id)!.AcpSessionId);
        Assert.Equal(new McpCaller(_id, 9), _tokens.Caller(token));
        var closing = _adapter.CloseAsync(session.Adapter, session.AcpSessionId);
        Assert.Equal("replacement", await agent.CloseAsync());
        await closing;
        await _adapter.ReleaseAsync(session.Adapter);
    }

    private SessionOpener Opener(Task? ready = null, Action? validate = null, Action? onRevoke = null) => new(_store, _launcher, _ => _adapter,
        new SessionMcp(ct => (ready ?? Task.CompletedTask).WaitAsync(ct),
            (id, observation) =>
            {
                Assert.Equal(_id, id);
                Assert.Same(_observed, observation);
                return _tokens.ServersFor(id, observation!.Epoch);
            }, id => { _tokens.Revoke(id); onRevoke?.Invoke(); }, (id, observation) =>
            {
                Assert.Equal(_id, id);
                Assert.Same(_observed, observation);
                validate?.Invoke();
            }));

    private static string Token(JsonElement open) => open.GetProperty("params").GetProperty("mcpServers")[0]
        .GetProperty("headers")[0].GetProperty("value").GetString()![7..];
    private const string Options = """[{"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"default","options":[{"value":"default","name":"Default"},{"value":"plan","name":"Plan"}]}]""";

    public async ValueTask DisposeAsync()
    {
        await _adapter.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
    }
}
