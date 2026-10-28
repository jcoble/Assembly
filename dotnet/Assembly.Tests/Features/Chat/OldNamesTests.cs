using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Serve;

namespace Assembly.Tests.Features.Chat;

/// <summary>A database made while sessions were called threads keeps everything when it's opened under the new names.</summary>
public sealed class OldNamesTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));

    public OldNamesTests()
    {
        // The tables as they were before the rename, with a session, its subagent child, two rows, a tool's details,
        // config, commands, a helper message and a preference.
        Run("""
            CREATE TABLE threads(thread_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
            CREATE TABLE rows(thread_id TEXT NOT NULL, seq INTEGER NOT NULL, run_id TEXT, item_id TEXT,
              kind TEXT NOT NULL, payload TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (thread_id, seq));
            CREATE INDEX rows_item ON rows(thread_id, item_id, kind, seq);
            CREATE TABLE tool_details(thread_id TEXT NOT NULL, item_id TEXT NOT NULL, input TEXT, output TEXT,
              diff TEXT, path TEXT, PRIMARY KEY (thread_id, item_id));
            CREATE TABLE thread_info(thread_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
              title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, machine TEXT,
              session_cost TEXT, parent_thread_id TEXT, helper INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX thread_info_updated ON thread_info(updated_at);
            CREATE TABLE thread_config(thread_id TEXT PRIMARY KEY, options TEXT, choices TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE agent_config(agent TEXT PRIMARY KEY, options TEXT NOT NULL);
            CREATE TABLE thread_commands(thread_id TEXT PRIMARY KEY, commands TEXT NOT NULL);
            CREATE TABLE agent_commands(agent TEXT PRIMARY KEY, commands TEXT NOT NULL);
            CREATE TABLE helper_messages(message_id TEXT PRIMARY KEY, from_thread TEXT NOT NULL, to_thread TEXT NOT NULL,
              text TEXT NOT NULL, run_id TEXT, created_at INTEGER NOT NULL, error TEXT, state TEXT, reply TEXT);
            CREATE INDEX helper_messages_pair ON helper_messages(from_thread, to_thread);
            CREATE TABLE preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version = 3;

            INSERT INTO threads VALUES ('s1', 4);
            INSERT INTO rows VALUES ('s1', 1, 'run-1', 'u', 'message', '{"blocks":[]}', 1),
              ('s1', 2, 'run-1', 't', 'tool', '{"summary":"ls"}', 2),
              ('s1', 3, 'run-1', 'subagent:acp-2', 'subagent', '{"threadId":"s2","title":"Child","state":"idle","canStop":false}', 3);
            INSERT INTO tool_details VALUES ('s1', 't', 'ls -la', 'a.txt', NULL, NULL);
            INSERT INTO thread_info(thread_id, agent, repo_url, title, acp_session_id, created_at, updated_at, parent_thread_id)
              VALUES ('s1', 'claude', 'r', 'Old one', 'acp-1', 1, 2, NULL), ('s2', 'claude', 'r', 'Child', 'acp-2', 1, 3, 's1');
            INSERT INTO thread_config VALUES ('s1', '[]', '{"model":"opus"}');
            INSERT INTO thread_commands VALUES ('s1', '[{"name":"context"}]');
            INSERT INTO helper_messages(message_id, from_thread, to_thread, text, created_at) VALUES ('m1', 's1', 's2', 'hi', 4);
            INSERT INTO preferences VALUES ('theme', '"dark"');
            """);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_db.Path + ".before-v4");
    }

    [Fact]
    public void Every_old_session_and_row_is_there_under_the_new_names()
    {
        var before = Schema();
        Open(out var rows, out var sessions).Dispose();
        var renamed = Schema();

        Assert.DoesNotContain("thread", renamed, StringComparison.OrdinalIgnoreCase);
        using (Open(out rows, out sessions))
        {
            Assert.Equal(["Old one"], sessions.List(before: null, limit: 10).Sessions.Select(session => session.Title));
            Assert.Equal("s1", sessions.Get("s2")!.ParentSessionId);
            Assert.Equal([1L, 2L, 3L], rows.RowsAfter("s1", 0).Select(row => row.Seq));
            Assert.Equal("""{"title":"Child","state":"idle","canStop":false,"sessionId":"s2"}""",
                rows.RowsAfter("s1", 2).Single().Payload.GetRawText());
            Assert.Equal(new StoredToolCall("ls", "ls -la", "a.txt", null, null), rows.ToolCall("s1", "t"));
            Assert.Equal("opus", sessions.Config("s1").Choices["model"]);
            Assert.Equal("""[{"name":"context"}]""", sessions.Config("s1").Commands);
            Assert.Equal(("s1", "s2"), (sessions.Message("m1")!.From, sessions.Message("m1")!.To));
            Assert.Equal(1, sessions.MessageCount("s1", "s2"));
            Assert.Equal(3, rows.LastSeq("s1"));
            // The rows' JSON changed with the names, so two machines on either side of the rename don't copy rows.
            Assert.Equal(4, rows.StoredFormatVersion);
        }

        // A second open changes nothing, and the preferences were never touched.
        Assert.Equal(renamed, Schema());
        Assert.NotEqual(before, renamed);
        Assert.Equal(["theme=\"dark\""], Query("SELECT key || '=' || value FROM preferences;"));
    }

    [Fact]
    public void Old_tables_an_older_build_made_again_after_the_rename_are_left_alone()
    {
        Open(out _, out _).Dispose();
        Run("""
            CREATE TABLE threads(thread_id TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
            CREATE TABLE thread_info(thread_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
              title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
            """);

        using (Open(out var rows, out var sessions))
        {
            Assert.Equal("Old one", sessions.Get("s1")!.Title);
            Assert.Equal(3, rows.LastSeq("s1"));
        }
    }

    [Fact]
    public void The_database_is_copied_as_it_was_before_the_rename()
    {
        Open(out _, out _).Dispose();

        var backup = _db.Path + ".before-v4";
        Assert.Equal(["3"], Query("SELECT CAST(user_version AS TEXT) FROM pragma_user_version;", backup));
        Assert.Equal(["Old one", "Child"], Query("SELECT title FROM thread_info ORDER BY updated_at;", backup));
        Assert.Equal(["3"], Query("SELECT CAST(COUNT(*) AS TEXT) FROM rows WHERE thread_id = 's1';", backup));
    }

    [Fact]
    public void A_copy_already_there_is_never_overwritten()
    {
        var backup = _db.Path + ".before-v4";
        File.WriteAllText(backup, "an earlier copy");

        using (Open(out _, out var sessions))
            Assert.Equal("Old one", sessions.Get("s1")!.Title);

        Assert.Equal("an earlier copy", File.ReadAllText(backup));
    }

    [Fact]
    public void A_copy_an_interrupted_open_left_half_written_is_made_again()
    {
        var backup = _db.Path + ".before-v4";
        File.WriteAllText(backup + ".partial", "half a copy");

        Open(out _, out _).Dispose();

        Assert.Equal(["Old one", "Child"], Query("SELECT title FROM thread_info ORDER BY updated_at;", backup));
        Assert.False(File.Exists(backup + ".partial"));
    }

    [Fact]
    public void A_database_the_first_rename_made_opens_as_it_is_with_no_copy()
    {
        // The rename as 627d704 shipped it (unchanged since) is what the box's data went through.
        Open(out _, out _).Dispose();
        File.Delete(_db.Path + ".before-v4");
        var renamed = Schema();
        var everything = Query("SELECT session_id || ':' || seq || ':' || payload FROM rows ORDER BY session_id, seq;");

        using (Open(out var rows, out var sessions))
        {
            Assert.Equal(["Old one"], sessions.List(before: null, limit: 10).Sessions.Select(session => session.Title));
            Assert.Equal("s1", sessions.Get("s2")!.ParentSessionId);
            Assert.Equal(4, rows.StoredFormatVersion);
        }

        Assert.Equal(renamed, Schema());
        Assert.Equal(everything, Query("SELECT session_id || ':' || seq || ':' || payload FROM rows ORDER BY session_id, seq;"));
        Assert.False(File.Exists(_db.Path + ".before-v4"));
    }

    [Fact]
    public void A_new_database_gets_no_copy()
    {
        using var fresh = new TempDatabase();
        using (Open(fresh.Path, out _, out _)) { }

        Assert.False(File.Exists(fresh.Path + ".before-v4"));
    }

    [Fact]
    public async Task A_copy_that_fails_leaves_the_database_and_the_old_checkouts_as_they_were()
    {
        var home = Directory.CreateTempSubdirectory("assembly-home-").FullName;
        try
        {
            var database = Path.Combine(home, "assembly.db");
            File.Copy(_db.Path, database);
            Directory.CreateDirectory(database + ".before-v4");
            Directory.CreateDirectory(Path.Combine(home, "threads"));
            File.WriteAllText(Path.Combine(home, "agents.json"), "[]");
            var before = Schema(database);

            await Assert.ThrowsAnyAsync<IOException>(() => DesktopServe.MainAsync(
                ["--home", home, "--token-file", Path.Combine(home, "env"), "--port", "0"]).WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(before, Schema(database));
            Assert.Equal(["3"], Query("SELECT CAST(user_version AS TEXT) FROM pragma_user_version;", database));
            Assert.True(Directory.Exists(Path.Combine(home, "threads")));
            Assert.False(Directory.Exists(Path.Combine(home, "sessions")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(home, recursive: true);
        }
    }

    private AppDatabase Open(out RowStore rows, out SessionStore sessions) => Open(_db.Path, out rows, out sessions);

    private AppDatabase Open(string path, out RowStore rows, out SessionStore sessions)
    {
        var database = TestDatabase.Open(path);
        rows = RowStore.Open(database);
        sessions = SessionStore.Open(database, _time, "checkouts");
        return database;
    }

    private string Schema(string? path = null) =>
        string.Join("\n", Query("SELECT type || ' ' || name || ': ' || sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name;", path));

    private List<string> Query(string sql, string? path = null)
    {
        using var db = new SqliteConnection($"Data Source={path ?? _db.Path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var found = new List<string>();
        while (reader.Read())
            found.Add(reader.GetString(0));
        return found;
    }

    private void Run(string sql)
    {
        using var db = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
