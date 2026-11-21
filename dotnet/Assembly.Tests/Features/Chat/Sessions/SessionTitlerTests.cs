using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Features.Chat.Sessions;

/// <summary>A new session is named by the utility model once its first turn has ended, here with a fake model.</summary>
public sealed class SessionTitlerTests : IAsyncDisposable
{
    private readonly TempDatabase _db = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("assembly-session-title-");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly RowStore _rows;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly FakeLauncher _launcher = new();
    private readonly SharedAdapter _adapter;
    private readonly SessionOpener _opener;
    private readonly SessionInfo _session;
    private readonly SharedLog _log = new();
    private readonly BackgroundWork _work;
    private readonly FakeModel _model = new("\n\n  \"Fix rail titles\"  \nWhy");
    private readonly SessionHost _host;

    public SessionTitlerTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _rows = RowStore.Open(_database);
        _hub = new LiveHub(_rows);
        _sessionStore = SessionStore.Open(_database, _time, _folder.FullName);
        _session = _sessionStore.Create("claude", "https://github.com/o/r", workingFolder: _folder.FullName);
        _adapter = new SharedAdapter(_launcher, "claude", _time, _log);
        _opener = new SessionOpener(_sessionStore, _launcher, _ => _adapter);
        _work = new BackgroundWork(_time, _log);
        _host = new SessionHost(_session.SessionId, _hub, _sessionStore, _opener, _time, _log, _work,
            titler: new SessionTitler(_sessionStore, _rows, _model));
    }

    [Fact]
    public async Task A_helper_title_replaces_the_prompt_title_once()
    {
        var agent = await TurnAsync("Fix the titles on the rail", "I renamed them.");
        await TurnAsync("And the tabs", "Done.", agent);
        await _work.StopAsync(TimeSpan.FromSeconds(5));

        var titled = _sessionStore.Get(_session.SessionId)!;
        Assert.Equal("Fix rail titles", titled.Title);
        Assert.Equal("helper", titled.TitleSource);
        // Asked once, about the first message and that turn's reply.
        var prompt = Assert.Single(_model.Prompts);
        Assert.EndsWith("<input>\nFix the titles on the rail\n\nI renamed them.\n</input>", prompt);
    }

    [Fact]
    public async Task A_user_title_is_never_overwritten()
    {
        _host.Send("Fix the titles on the rail");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        _sessionStore.SetTitle(_session.SessionId, "Mine", "user");
        await agent.TextAsync("I renamed them.");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await _work.StopAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_model.Prompts);
        Assert.Equal(("Mine", "user"), (_sessionStore.Get(_session.SessionId)!.Title, _sessionStore.Get(_session.SessionId)!.TitleSource));
    }

    [Fact]
    public async Task A_cancelled_first_turn_starts_no_title()
    {
        var runId = _host.Send("Fix the titles on the rail")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.True(_host.Stop(runId));
        await agent.ReadAsync("session/cancel");
        await agent.FinishAsync(prompt, """{"stopReason":"cancelled"}""");
        await agent.CloseAsync();
        await _work.StopAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_model.Prompts);
        Assert.Equal("Fix the titles on the rail", _sessionStore.Get(_session.SessionId)!.Title);
    }

    [Fact]
    public async Task A_failed_first_turn_starts_no_title()
    {
        _host.Send("Fix the titles on the rail");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.ReplyErrorAsync(prompt, -32603, "boom");
        await agent.CloseAsync();
        await _work.StopAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_model.Prompts);
        Assert.Equal("Fix the titles on the rail", _sessionStore.Get(_session.SessionId)!.Title);
    }

    [Fact]
    public async Task A_stopped_first_turn_is_titled_when_a_later_turn_ends_normally()
    {
        var runId = _host.Send("Fix the titles on the rail")!;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.True(_host.Stop(runId));
        await agent.ReadAsync("session/cancel");
        await agent.FinishAsync(prompt, """{"stopReason":"cancelled"}""");
        await agent.CloseAsync();

        await TurnAsync("Fix the titles on the rail, please", "I renamed them.", agent);
        await _work.StopAsync(TimeSpan.FromSeconds(5));

        var titled = _sessionStore.Get(_session.SessionId)!;
        Assert.Equal(("Fix rail titles", "helper"), (titled.Title, titled.TitleSource));
        Assert.Single(_model.Prompts);
    }

    // One turn, answered with one message. The first opens the session; a later one is passed the same agent.
    private async Task<FakeLink> TurnAsync(string text, string reply, FakeLink? agent = null)
    {
        _host.Send(text);
        if (agent is null)
        {
            agent = await _launcher.NextAsync();
            await agent.StartAsync();
        }
        else
        {
            await agent.OpenAsync();
        }

        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync(reply);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        var turn = _host.StopForShutdownAsync();
        await _adapter.DisposeAsync();
        await turn;
        await _work.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
        _folder.Delete(recursive: true);
    }

    private sealed class FakeModel(string? answer) : IUtilityModel
    {
        private readonly List<string> _prompts = [];

        public IReadOnlyList<string> Prompts
        {
            get
            {
                lock (_prompts)
                    return [.. _prompts];
            }
        }

        public Task<UtilityResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
        {
            lock (_prompts)
                _prompts.Add(prompt);
            return Task.FromResult(new UtilityResult(answer is null ? UtilityStatus.Failed : UtilityStatus.Succeeded, answer, answer is null ? "fixture_failure" : null));
        }
    }
}
