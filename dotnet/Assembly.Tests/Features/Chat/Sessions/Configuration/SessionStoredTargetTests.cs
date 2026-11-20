using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Transcript;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions.Configuration;

/// <summary>A session opens after a restart or an adapter update; only a target the composer shows is checked.</summary>
public sealed class SessionStoredTargetTests : IAsyncDisposable
{
    private readonly TempDatabase _temp = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stored-target-" + Guid.NewGuid().ToString("N"));
    private readonly BackgroundWork _work;
    private readonly AppDatabase _db;
    private readonly LiveHub _hub;
    private readonly SessionStore _store;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SharedAdapter _pool;
    private readonly SessionOpener _opener;
    private SessionHost? _host;
    private string Target => ConfigTargetIdentity.Capture(_launcher, "claude").TargetId;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public SessionStoredTargetTests()
    {
        Directory.CreateDirectory(_folder);
        _db = TestDatabase.Open(_temp.Path);
        _hub = new LiveHub(RowStore.Open(_db));
        _store = SessionStore.Open(_db, _time, "/work");
        _pool = new SharedAdapter(_launcher, "claude", _time, TextWriter.Null);
        _opener = new SessionOpener(_store, _launcher, _ => _pool);
        _work = new BackgroundWork(_time, TextWriter.Null);
    }

    [Fact]
    public async Task A_session_from_before_a_restart_takes_the_target_shown_after_it_when_inherited_variables_differ()
    {
        const string key = "ASSEMBLY_STORED_TARGET_INHERITED_FIXTURE";
        var prior = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "first-login");
            var host = Opened(Target);
            Environment.SetEnvironmentVariable(key, "second-login");
            await AssertSettingsRead(host, Target);
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }

    [Fact]
    public async Task A_session_stored_with_an_earlier_target_opens_with_its_agents_current_configuration()
    {
        var host = Opened("target-stored-before-this-upgrade");
        _launcher.Agents = [new("claude", "Claude Code", "claude-agent-acp", ["/adapters/0.110.0-b2/index.js"], null)];
        await AssertSettingsRead(host, Target);
    }

    [Fact]
    public async Task A_target_shown_before_the_agents_configuration_changed_is_refused()
    {
        var host = Opened(Target);
        var shown = Target;
        _launcher.Agents = [new("claude", "Claude Code", "claude-agent-acp", ["--updated"], null)];
        var refused = await Assert.ThrowsAsync<CallFailed>(() => host.WarmConfigAsync(shown, CancellationToken.None));
        Assert.Equal("The selected agent configuration changed. Select it again.", refused.Message);
        Assert.Equal(0, _launcher.Launches);
    }

    private SessionHost Opened(string storedTarget)
    {
        var session = _store.Create("claude", "repo", new(storedTarget, new Dictionary<string, string> { ["model"] = "b" }), workingFolder: _folder);
        _store.SetAcpSession(session.SessionId, "saved-B");
        return _host = new SessionHost(session.SessionId, _hub, _store, _opener, _time, TextWriter.Null, _work);
    }

    private async Task AssertSettingsRead(SessionHost host, string shown)
    {
        var warm = host.WarmConfigAsync(shown, CancellationToken.None);
        var agent = await _launcher.NextAsync();
        var open = await agent.StartAsync(acpSessionId: "saved-B", configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        Assert.Equal(SessionWarmConfigTests.Options, (await warm.WaitAsync(Wait)).Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { } host) await Task.WhenAll(host.StopWarmForShutdownAsync(), host.StopForShutdownAsync());
        await _pool.DisposeAsync();
        await _work.DisposeAsync();
        _hub.Dispose(); _db.Dispose(); _temp.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
