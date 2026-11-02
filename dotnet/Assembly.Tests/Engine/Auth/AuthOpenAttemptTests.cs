using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Agents.Auth;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthOpenAttemptTests
{
    [Fact]
    public async Task Genuine_resume_auth_rejection_cannot_fall_back_to_a_new_ACP_identity()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); RowStore.Open(db);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var store = SessionStore.Open(db, time, "checkouts"); var session = store.Create("claude", "https://github.com/o/r");
        store.SetAcpSession(session.SessionId, "saved-prior-acp");
        var launcher = new FakeLauncher(); await using var adapter = new SharedAdapter(launcher, "claude", time, TextWriter.Null);
        var opener = new SessionOpener(store, launcher, _ => adapter);
        var capture = new AuthOpenAttempt();
        var opening = opener.OpenAsync(session.SessionId, "work", new StageTimes(time), default, authAttempt: capture);
        var agent = await launcher.NextAsync(); Task<JsonElement>? unexpected = null;
        try
        {
            var initialize = await agent.ReadAsync("initialize");
            await agent.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");
            var resume = await agent.ReadAsync("session/resume");
            Assert.Equal("saved-prior-acp", resume.GetProperty("params").GetProperty("sessionId").GetString());
            await agent.ReplyErrorAsync(resume, -32000, "Sign in");
            unexpected = agent.NextMessageAsync();
            Assert.Same(opening, await Task.WhenAny(opening, unexpected).WaitAsync(TimeSpan.FromSeconds(5)));
            var error = await Assert.ThrowsAsync<JsonRpcException>(async () => await opening);
            Assert.Equal(-32000, error.Code); Assert.Equal(1, launcher.Launches);
            Assert.Equal(AuthOpenFailureStage.ResumeSession, capture.Failure); Assert.False(capture.IdentityAccepted);
            Assert.Equal("saved-prior-acp", store.Get(session.SessionId)!.AcpSessionId);
        }
        finally
        {
            agent.Crash();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (unexpected is not null) { try { await unexpected.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_the_actual_initialize_or_new_response_stamps_a_pre_identity_auth_rejection(bool initialize)
    {
        await using var f = new OpeningFixture(); var opening = f.Open(); var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!initialize) { await Initialize(agent, request); request = await agent.ReadAsync("session/new"); }
            await agent.ReplyErrorAsync(request, -32000, "Sign in");
            Assert.Equal(-32000, (await Assert.ThrowsAsync<JsonRpcException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)))).Code);
            Assert.Equal(initialize ? AuthOpenFailureStage.Initialize : AuthOpenFailureStage.NewSession, f.Capture.Failure);
            Assert.False(f.Capture.IdentityAccepted); Assert.Null(f.Store.Get(f.SessionId)!.AcpSessionId);
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Non_auth_initialize_or_new_errors_never_acquire_an_auth_stage(bool initialize)
    {
        await using var f = new OpeningFixture(); var opening = f.Open(); var agent = await f.Launcher.NextAsync();
        try
        {
            var request = await agent.ReadAsync("initialize");
            if (!initialize) { await Initialize(agent, request); request = await agent.ReadAsync("session/new"); }
            await agent.ReplyErrorAsync(request, -32603, "Other failure");
            Assert.Equal(-32603, (await Assert.ThrowsAsync<JsonRpcException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)))).Code);
            Assert.Null(f.Capture.Failure); Assert.False(f.Capture.IdentityAccepted);
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task A_launcher_exception_cannot_forge_an_initialize_response_stamp()
    {
        await using var f = new OpeningFixture(); var actual = new JsonRpcException(-32000, "Launcher failure");
        f.Launcher.Launch = _ => Task.FromException<IAgentLink>(actual);
        Assert.Same(actual, await Assert.ThrowsAsync<JsonRpcException>(async () => await f.Open()));
        Assert.Null(f.Capture.Failure); Assert.False(f.Capture.IdentityAccepted);
    }

    [Fact]
    public async Task Non_auth_resume_failure_keeps_the_existing_new_session_fallback_and_marks_its_real_acceptance()
    {
        await using var f = new OpeningFixture(resume: true); var opening = f.Open(); var agent = await f.Launcher.NextAsync();
        try
        {
            await Initialize(agent, await agent.ReadAsync("initialize"));
            await agent.ReplyErrorAsync(await agent.ReadAsync("session/resume"), -32603, "Transcript missing");
            var fresh = await agent.ReadAsync("session/new"); Assert.Null(f.Capture.Failure); Assert.False(f.Capture.IdentityAccepted);
            await agent.ReplyAsync(fresh, """{"sessionId":"fresh-acp"}""");
            var opened = await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Capture.IdentityAccepted); Assert.Null(f.Capture.Failure); Assert.Equal("fresh-acp", opened.AcpSessionId);
            var closing = opened.Pool.CloseAsync(opened.Adapter, opened.AcpSessionId); await agent.CloseAsync(); await closing;
            await opened.Pool.ReleaseAsync(opened.Adapter);
        }
        finally { agent.Crash(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_auth_error_after_new_or_resume_acceptance_never_becomes_a_failed_open_stage(bool resume)
    {
        await using var f = new OpeningFixture(resume, rejectAfterIdentity: true); var opening = f.Open(); var agent = await f.Launcher.NextAsync();
        try
        {
            await Initialize(agent, await agent.ReadAsync("initialize"));
            var request = await agent.ReadAsync(resume ? "session/resume" : "session/new");
            await agent.ReplyAsync(request, resume ? "{}" : """{"sessionId":"accepted-acp"}""");
            await agent.CloseAsync();
            Assert.Same(f.PostIdentityError, await Assert.ThrowsAsync<JsonRpcException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.True(f.Capture.IdentityAccepted); Assert.Null(f.Capture.Failure);
        }
        finally { agent.Crash(); }
    }

    [Fact]
    public async Task Cancelled_open_still_marks_its_late_real_identity_before_the_existing_close()
    {
        await using var f = new OpeningFixture(); using var stop = new CancellationTokenSource();
        var opening = f.Open(stop.Token); var agent = await f.Launcher.NextAsync();
        try
        {
            await Initialize(agent, await agent.ReadAsync("initialize")); var request = await agent.ReadAsync("session/new");
            stop.Cancel(); await agent.ReplyAsync(request, """{"sessionId":"late-acp"}""");
            await agent.CloseAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(f.Capture.IdentityAccepted); Assert.Null(f.Capture.Failure);
        }
        finally { agent.Crash(); }
    }

    private static Task Initialize(FakeLink agent, JsonElement request) => agent.ReplyAsync(request,
        """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[]}""");

    private sealed class OpeningFixture : IAsyncDisposable
    {
        private readonly TempDatabase _file = new();
        private readonly Assembly.Infrastructure.Persistence.AppDatabase _db;
        private readonly SharedAdapter _adapter;
        private readonly SessionOpener _opener;
        private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        internal FakeLauncher Launcher { get; } = new();
        internal AuthOpenAttempt Capture { get; } = new();
        internal SessionStore Store { get; }
        internal string SessionId { get; }
        internal JsonRpcException PostIdentityError { get; } = new(-32000, "After accepted identity");
        internal OpeningFixture(bool resume = false, bool rejectAfterIdentity = false)
        {
            _db = TestDatabase.Open(_file.Path); RowStore.Open(_db); Store = SessionStore.Open(_db, _time, "checkouts");
            SessionId = Store.Create("claude", "https://github.com/o/r").SessionId;
            if (resume) Store.SetAcpSession(SessionId, "saved-prior-acp");
            _adapter = new(Launcher, "claude", _time, TextWriter.Null);
            var mcp = rejectAfterIdentity ? new SessionMcp(_ => Task.CompletedTask, (_, _) => [], _ => { }, (_, _) => throw PostIdentityError) : null;
            _opener = new(Store, Launcher, _ => _adapter, mcp);
        }
        internal Task<OpenSession> Open(CancellationToken ct = default) => _opener.OpenAsync(SessionId, "work", new(_time), ct, authAttempt: Capture);
        public async ValueTask DisposeAsync() { try { await _adapter.DisposeAsync(); } finally { _db.Dispose(); _file.Dispose(); } }
    }
}
