using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class ProviderAdapterTests : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private readonly TempDatabase _db = new();
    private readonly AppDatabase _database;
    private readonly RowStore _rows;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessions;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly BackgroundWork _work;
    private readonly SessionRegistry _registry;

    public ProviderAdapterTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _rows = RowStore.Open(_database);
        _hub = new LiveHub(_rows);
        _sessions = SessionStore.Open(_database, _time, "checkouts");
        _work = new BackgroundWork(_time, TextWriter.Null);
        _registry = new(_hub, _sessions, _launcher, _time, TextWriter.Null, _work);
    }

    private IProviderAdapters Providers => Assert.IsAssignableFrom<IProviderAdapters>(_registry);
    private (AgentDescriptor Descriptor, string Target) Target()
    {
        var captured = ConfigTargetIdentity.Capture(_launcher, "claude");
        return (captured.Descriptor with { Env = captured.Environment }, captured.TargetId);
    }

    [Fact]
    public async Task Probe_borrows_one_temporary_ACP_session_without_creating_an_Assembly_session()
    {
        var providers = Providers;
        var target = Target();
        var probing = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", default);
        var link = await _launcher.NextAsync();
        var opened = await link.StartAsync("probe-only");
        Assert.Equal("/isolated-data/probe", opened.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Empty(opened.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("probe-only", await link.CloseAsync());
        var result = await probing.WaitAsync(Limit);
        Assert.NotNull(result.Initialize);
        Assert.Equal("probe-only", result.Session!.SessionId);
        Assert.False(result.AuthRequired);
        Assert.Null(result.ProblemCode);
        Assert.Empty(_registry.OpenSessions);
        Assert.Empty(_sessions.List(null, 10).Sessions);
        Assert.Empty(_launcher.CheckedOut);
        Assert.False(link.Disposed);
    }

    [Fact]
    public async Task A_probe_preserves_an_active_prompt_and_only_closes_its_own_temporary_session()
    {
        var providers = Providers;
        var session = _sessions.Create("claude", "https://example.invalid/repo");
        var host = _registry.For(session.SessionId);
        var run = host.Send("continue the original work");
        var link = await _launcher.NextAsync();
        await link.StartAsync();
        var prompt = await link.ReadAsync("session/prompt");
        var before = _rows.RowsAfter(session.SessionId, 0).Count;
        var target = Target();
        var probing = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", default);
        await link.OpenAsync("probe-only");
        Assert.Equal("probe-only", await link.CloseAsync());
        await probing.WaitAsync(Limit);
        Assert.Equal(1, _launcher.Launches);
        Assert.Equal(run, host.ActiveRunId);
        Assert.Equal(before, _rows.RowsAfter(session.SessionId, 0).Count);
        Assert.Equal(FakeLink.AcpSessionId, _sessions.Get(session.SessionId)!.AcpSessionId);
        Assert.False(link.Disposed);
        await link.FinishAsync(prompt, "{\"stopReason\":\"end_turn\"}");
        Assert.Equal(FakeLink.AcpSessionId, await link.CloseAsync());
    }

    [Fact]
    public async Task Cancellation_closes_the_exact_late_new_session_and_preserves_the_shared_link()
    {
        var providers = Providers;
        var target = Target();
        using var stop = new CancellationTokenSource();
        var probing = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", stop.Token);
        var link = await _launcher.NextAsync();
        var initialize = await link.ReadAsync("initialize");
        await link.ReplyAsync(initialize, "{\"protocolVersion\":2}");
        var opening = await link.ReadAsync("session/new");
        stop.Cancel();
        await link.ReplyAsync(opening, "{\"sessionId\":\"late-probe\"}");
        Assert.Equal("late-probe", await link.CloseAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing.WaitAsync(Limit));
        Assert.False(link.Disposed);
        Assert.NotNull(await providers.CaptureAsync("claude", target.Target, default));
    }

    [Fact]
    public async Task Exact_capture_retirement_stops_zero_borrows_and_cannot_stop_a_replacement()
    {
        var providers = Providers;
        var target = Target();
        var firstProbe = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", default);
        var old = await _launcher.NextAsync();
        await old.StartAsync("first-probe");
        await old.CloseAsync();
        await firstProbe.WaitAsync(Limit);
        var captured = Assert.IsAssignableFrom<ProviderAdapterCapture>(await providers.CaptureAsync("claude", target.Target, default));
        await providers.RetireAsync(captured, default);
        Assert.True(old.Disposed);
        Assert.True(old.Exited.IsCompleted);
        var nextProbe = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", default);
        var replacement = await _launcher.NextAsync();
        await replacement.StartAsync("second-probe");
        await replacement.CloseAsync();
        await nextProbe.WaitAsync(Limit);
        await providers.RetireAsync(captured, default);
        Assert.False(replacement.Disposed);
        Assert.Equal(2, _launcher.Launches);
    }

    [Fact]
    public async Task Shutdown_cancels_a_probe_blocked_in_initialize_and_joins_its_exact_link()
    {
        var providers = Providers;
        var target = Target();
        var probing = providers.ProbeAsync(target.Descriptor, target.Target, "/isolated-data/probe", default);
        var link = await _launcher.NextAsync();
        await link.ReadAsync("initialize");
        await _registry.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing.WaitAsync(Limit));
        Assert.True(link.Disposed);
        Assert.True(link.Exited.IsCompleted);
        Assert.Equal(1, _launcher.Launches);
    }

    public async ValueTask DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _work.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
    }
}
