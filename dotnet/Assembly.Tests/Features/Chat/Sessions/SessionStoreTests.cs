using Microsoft.Extensions.Time.Testing;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionStoreTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly SessionStore _sessionStore;

    public SessionStoreTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _sessionStore = SessionStore.Open(_database, _time);
    }

    [Fact]
    public void A_new_session_has_no_title_or_agent_session_yet()
    {
        var created = _sessionStore.Create("claude", "https://github.com/o/r");

        Assert.Equal(new SessionInfo(created.SessionId, "claude", "https://github.com/o/r", null, null, 1_000_000), _sessionStore.Get(created.SessionId));
        Assert.True(Guid.TryParse(created.SessionId, out _));
        Assert.Null(_sessionStore.Get("nope"));
    }

    [Fact]
    public void A_list_read_for_a_caller_who_has_gone_stops_with_nothing_read()
    {
        Create();
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _sessionStore.List(before: null, limit: 2, gone.Token));
    }

    [Fact]
    public void The_list_is_newest_first_and_pages_by_updated_at()
    {
        var a = Create();
        var b = Create();
        var c = Create();

        var first = _sessionStore.List(before: null, limit: 2);
        Assert.Equal([c.SessionId, b.SessionId], first.Sessions.Select(t => t.SessionId));
        Assert.Equal(b.UpdatedAt, first.NextBefore);

        var second = _sessionStore.List(first.NextBefore, limit: 2);
        Assert.Equal([a.SessionId], second.Sessions.Select(t => t.SessionId));
        Assert.Null(second.NextBefore);
    }

    [Fact]
    public void Sending_moves_a_session_to_the_top()
    {
        var a = Create();
        Create();
        _time.Advance(TimeSpan.FromSeconds(1));
        _sessionStore.Touch(a.SessionId);

        Assert.Equal(a.SessionId, _sessionStore.List(null, 10).Sessions[0].SessionId);
    }

    [Fact]
    public void Title_and_agent_session_are_kept()
    {
        var a = Create();
        _sessionStore.SetTitle(a.SessionId, "Fix the build");
        _sessionStore.SetAcpSession(a.SessionId, "acp-1");

        var saved = _sessionStore.Get(a.SessionId)!;
        Assert.Equal("Fix the build", saved.Title);
        Assert.Equal("acp-1", saved.AcpSessionId);
    }

    [Fact]
    public void It_shares_a_database_file_with_the_rows()
    {
        using var database = TestDatabase.Open(_db.Path);
        var rows = RowStore.Open(database);
        var a = Create();
        var reopened = SessionStore.Open(database, _time);

        Assert.Equal(a, reopened.Get(a.SessionId));
    }

    [Fact]
    public void A_remote_session_is_kept_under_the_boxs_id_with_its_machine()
    {
        var copy = _sessionStore.Add("box-session", "claude", "/home/me/repo", "workbox-test");

        Assert.Equal(new SessionInfo("box-session", "claude", "/home/me/repo", null, null, 1_000_000, "workbox-test"), copy);
        Assert.Equal(copy, _sessionStore.Get("box-session"));
        Assert.Null(Create().Machine);
    }

    [Fact]
    public void An_older_database_gets_the_machine_and_session_cost_columns()
    {
        // The box's database was made before a session had a machine.
        using var olderDb = new TempDatabase();
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={olderDb.Path};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE thread_info(thread_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
                  title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                INSERT INTO thread_info VALUES ('old', 'claude', 'r', 'Old one', 'acp-1', 1, 2);
                """;
            command.ExecuteNonQuery();
        }

        using var sessionsDatabase = TestDatabase.Open(olderDb.Path);
        var sessionStore = SessionStore.Open(sessionsDatabase, _time);
        Assert.Equal(new SessionInfo("old", "claude", "r", "Old one", "acp-1", 2), sessionStore.Get("old"));
        Assert.Equal("workbox-test", sessionStore.Add("new", "claude", "r", "workbox-test").Machine);
        sessionStore.SetSessionCost("old", 0.2238768m);
        Assert.Equal(0.2238768m, sessionStore.Get("old")!.SessionCost);
    }

    private SessionInfo Create()
    {
        _time.Advance(TimeSpan.FromMilliseconds(10));
        return _sessionStore.Create("claude", "https://github.com/o/r");
    }

    [Fact]
    public void A_sessions_commands_are_its_own_and_a_new_session_starts_with_its_agents_latest()
    {
        var first = _sessionStore.Create("claude", "repo");
        var codex = _sessionStore.Create("codex", "repo");
        Assert.Null(_sessionStore.Config(first.SessionId).Commands);
        Assert.Equal(new Dictionary<string, string>(), _sessionStore.Config(first.SessionId).Choices);

        _sessionStore.SetCommands(first.SessionId, """[{"name":"context"}]""");
        var second = _sessionStore.Create("claude", "repo");
        Assert.Equal("""[{"name":"context"}]""", _sessionStore.Config(second.SessionId).Commands);
        Assert.Null(_sessionStore.Config(codex.SessionId).Commands);

        _sessionStore.SetCommands(second.SessionId, """[{"name":"review"}]""");
        Assert.Equal("""[{"name":"context"}]""", _sessionStore.Config(first.SessionId).Commands);
        Assert.Equal("""[{"name":"review"}]""", _sessionStore.Config(_sessionStore.Create("claude", "repo").SessionId).Commands);
    }

    [Fact]
    public void A_subagent_keeps_one_session_under_its_parent_for_every_delegation()
    {
        var parent = _sessionStore.Create("claude", "repo");
        var child = _sessionStore.Child(parent, "child1", "general-purpose");
        Assert.Equal((parent.SessionId, "child1", "general-purpose", "claude"), (child.ParentSessionId, child.AcpSessionId, child.Title, child.Agent));
        Assert.Equal(child.SessionId, _sessionStore.Child(parent, "child1", "again").SessionId);
        Assert.NotEqual(child.SessionId, _sessionStore.Child(_sessionStore.Create("claude", "repo"), "child1", "other").SessionId);
        Assert.Equal(parent.SessionId, _sessionStore.Get(child.SessionId)!.ParentSessionId);
        Assert.DoesNotContain(_sessionStore.List(null, 50).Sessions, session => session.ParentSessionId is not null);
    }

    public void Dispose()
    {
        _database.Dispose();
        _db.Dispose();
    }
}
