using Microsoft.Extensions.Time.Testing;
using Assembly.Engine.Persistence;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Tests.Threads;

public sealed class ThreadStoreTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly ThreadStore _threads;

    public ThreadStoreTests()
    {
        _database = AppDatabase.Open(_db.Path);
        _threads = ThreadStore.Open(_database, _time);
    }

    [Fact]
    public void A_new_thread_has_no_title_or_agent_session_yet()
    {
        var created = _threads.Create("claude", "https://github.com/o/r");

        Assert.Equal(new ThreadInfo(created.ThreadId, "claude", "https://github.com/o/r", null, null, 1_000_000), _threads.Get(created.ThreadId));
        Assert.True(Guid.TryParse(created.ThreadId, out _));
        Assert.Null(_threads.Get("nope"));
    }

    [Fact]
    public void The_list_is_newest_first_and_pages_by_updated_at()
    {
        var a = Create();
        var b = Create();
        var c = Create();

        var first = _threads.List(before: null, limit: 2);
        Assert.Equal([c.ThreadId, b.ThreadId], first.Threads.Select(t => t.ThreadId));
        Assert.Equal(b.UpdatedAt, first.NextBefore);

        var second = _threads.List(first.NextBefore, limit: 2);
        Assert.Equal([a.ThreadId], second.Threads.Select(t => t.ThreadId));
        Assert.Null(second.NextBefore);
    }

    [Fact]
    public void Sending_moves_a_thread_to_the_top()
    {
        var a = Create();
        Create();
        _time.Advance(TimeSpan.FromSeconds(1));
        _threads.Touch(a.ThreadId);

        Assert.Equal(a.ThreadId, _threads.List(null, 10).Threads[0].ThreadId);
    }

    [Fact]
    public void Title_and_agent_session_are_kept()
    {
        var a = Create();
        _threads.SetTitle(a.ThreadId, "Fix the build");
        _threads.SetAcpSession(a.ThreadId, "acp-1");

        var saved = _threads.Get(a.ThreadId)!;
        Assert.Equal("Fix the build", saved.Title);
        Assert.Equal("acp-1", saved.AcpSessionId);
    }

    [Fact]
    public void It_shares_a_database_file_with_the_rows()
    {
        using var database = AppDatabase.Open(_db.Path);
        var rows = RowStore.Open(database);
        var a = Create();
        var reopened = ThreadStore.Open(database, _time);

        Assert.Equal(a, reopened.Get(a.ThreadId));
    }

    [Fact]
    public void A_remote_thread_is_kept_under_the_boxs_id_with_its_machine()
    {
        var copy = _threads.Add("box-thread", "claude", "/home/me/repo", "workbox-test");

        Assert.Equal(new ThreadInfo("box-thread", "claude", "/home/me/repo", null, null, 1_000_000, "workbox-test"), copy);
        Assert.Equal(copy, _threads.Get("box-thread"));
        Assert.Null(Create().Machine);
    }

    [Fact]
    public void An_older_database_gets_the_machine_and_session_cost_columns()
    {
        // The box's database was made before a thread had a machine.
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

        using var threadsDatabase = AppDatabase.Open(olderDb.Path);
        var threads = ThreadStore.Open(threadsDatabase, _time);
        Assert.Equal(new ThreadInfo("old", "claude", "r", "Old one", "acp-1", 2), threads.Get("old"));
        Assert.Equal("workbox-test", threads.Add("new", "claude", "r", "workbox-test").Machine);
        threads.SetSessionCost("old", 0.2238768m);
        Assert.Equal(0.2238768m, threads.Get("old")!.SessionCost);
    }

    private ThreadInfo Create()
    {
        _time.Advance(TimeSpan.FromMilliseconds(10));
        return _threads.Create("claude", "https://github.com/o/r");
    }

    [Fact]
    public void A_threads_commands_are_its_own_and_a_new_thread_starts_with_its_agents_latest()
    {
        var first = _threads.Create("claude", "repo");
        var codex = _threads.Create("codex", "repo");
        Assert.Null(_threads.Config(first.ThreadId).Commands);
        Assert.Equal(new Dictionary<string, string>(), _threads.Config(first.ThreadId).Choices);

        _threads.SetCommands(first.ThreadId, """[{"name":"context"}]""");
        var second = _threads.Create("claude", "repo");
        Assert.Equal("""[{"name":"context"}]""", _threads.Config(second.ThreadId).Commands);
        Assert.Null(_threads.Config(codex.ThreadId).Commands);

        _threads.SetCommands(second.ThreadId, """[{"name":"review"}]""");
        Assert.Equal("""[{"name":"context"}]""", _threads.Config(first.ThreadId).Commands);
        Assert.Equal("""[{"name":"review"}]""", _threads.Config(_threads.Create("claude", "repo").ThreadId).Commands);
    }

        [Fact]
    public void A_subagent_keeps_one_thread_under_its_parent_for_every_delegation()
    {
        var parent = _threads.Create("claude", "repo");
        var child = _threads.Child(parent, "child1", "general-purpose");
        Assert.Equal((parent.ThreadId, "child1", "general-purpose", "claude"), (child.ParentThreadId, child.AcpSessionId, child.Title, child.Agent));
        Assert.Equal(child.ThreadId, _threads.Child(parent, "child1", "again").ThreadId);
        Assert.NotEqual(child.ThreadId, _threads.Child(_threads.Create("claude", "repo"), "child1", "other").ThreadId);
        Assert.Equal(parent.ThreadId, _threads.Get(child.ThreadId)!.ParentThreadId);
        Assert.DoesNotContain(_threads.List(null, 50).Threads, thread => thread.ParentThreadId is not null);
    }

        public void Dispose()
    {
        _database.Dispose();
        _db.Dispose();
    }
}
