using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Socket;

/// <summary>Saves (plan 5.9, B6): each change written as it happens, one write each, through a real writer.</summary>
public sealed partial class SocketTests
{
    [Fact]
    public async Task One_save_makes_one_write_with_one_audit_row_and_its_change_before_its_result()
    {
        using var database = Settings();
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"settings"}""");
        await ReadAsync(socket);

        await SaveAsync(socket, 2, "theme", "\"dark\"");

        Assert.Equal("""{"t":"change","sub":1,"op":"upsert","key":"theme","item":{"key":"theme","value":"dark"}}""", await ReadAsync(socket));
        Assert.Equal("""{"t":"result","id":2,"result":null}""", await ReadAsync(socket));
        Assert.Equal(["settings/set theme"], Audit(database.Path));
        Assert.Equal("dark", Value(database.Path, "theme"));
    }

    [Fact]
    public async Task Fifty_saves_of_one_key_sent_at_once_end_on_the_last_value()
    {
        using var database = Settings();
        using var socket = await OpenAsync();

        for (var i = 1; i <= 50; i++)
            await SaveAsync(socket, i, "theme", $"\"v{i}\"");
        // Commits are in order; answers can pass each other.
        var answers = await ReadByIdAsync(socket, 50);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $$"""{"t":"result","id":{{i}},"result":null}"""), answers);

        Assert.Equal("v50", Value(database.Path, "theme"));
        Assert.Equal(50, Audit(database.Path).Count);
    }

    [Fact]
    public async Task A_slow_save_doesnt_hold_up_a_read_on_the_same_socket()
    {
        using var slow = new ManualResetEventSlim();
        using var database = Settings(before: () => slow.Wait(Wait));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await SaveAsync(socket, 1, "theme", "\"dark\"");
        await CallAsync(socket, 2, "notes", "echo", """{"text":"quick"}""");

        Assert.Equal("""{"t":"result","id":2,"result":{"text":"quick"}}""", await ReadAsync(socket));
        slow.Set();
        Assert.Equal("""{"t":"result","id":1,"result":null}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_failed_save_reports_once_and_leaves_the_committed_value()
    {
        using var database = Settings();
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();
        await SaveAsync(socket, 1, "theme", "\"dark\"");
        await ReadAsync(socket);
        await SendAsync(socket, """{"t":"sub","id":2,"topic":"settings"}""");
        await ReadAsync(socket);

        await SaveAsync(socket, 3, "theme", "\"refused\"");
        Assert.Equal("failed", Code(await ReadAsync(socket), id: 3));
        // No change for it, and no second answer: the echo is next.
        await CallAsync(socket, 4, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":4,"result":{"text":""}}""", await ReadAsync(socket));
        Assert.Equal("dark", Value(database.Path, "theme"));
        Assert.Equal(["settings/set theme"], Audit(database.Path));
    }

    [Fact]
    public async Task Saves_still_in_the_write_queue_commit_before_the_backend_stops()
    {
        using var held = new ManualResetEventSlim();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var database = Settings(before: () =>
        {
            writing.TrySetResult();
            held.Wait(Wait);
        });
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();
        await SaveAsync(socket, 1, "theme", "\"dark\"");
        await SaveAsync(socket, 2, "font", "\"mono\"");
        // Frames are read in order, so once this answers, the second save is queued; the first is in its write.
        await CallAsync(socket, 3, "notes", "echo", "{}");
        Assert.Equal("""{"t":"result","id":3,"result":{"text":""}}""", await ReadAsync(socket));
        await writing.Task.WaitAsync(Wait);

        var stopping = _app.StopAsync();
        Assert.False(stopping.IsCompleted);
        held.Set();
        await stopping.WaitAsync(Wait);

        Assert.Equal(["settings/set theme", "settings/set font"], Audit(database.Path));
        Assert.Equal("mono", Value(database.Path, "font"));
    }

    [Fact]
    public async Task A_saved_value_goes_into_no_audit_row_and_no_log()
    {
        const string Marker = "marker-7f3a-not-for-logs";
        using var database = Settings();
        using var socket = await OpenAsync();

        await SaveAsync(socket, 1, "notion-token", $"\"{Marker}\"");
        await SaveAsync(socket, 2, "theme", $"\"refused {Marker}\"");
        // Bad args: an object where the value is a string.
        await SaveAsync(socket, 3, "theme", $$"""{"text":"{{Marker}}"}""");
        await SaveAsync(socket, 4, "theme", $"\"crash {Marker}\"");
        var answers = await ReadByIdAsync(socket, 4);
        Assert.Equal("bad_args", Code(answers[2], id: 3));
        Assert.Equal("failed", Code(answers[3], id: 4));
        Assert.DoesNotContain(Marker, answers[3]);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error);

        Assert.Equal(Marker, Value(database.Path, "notion-token"));
        Assert.DoesNotContain(Audit(database.Path), what => what.Contains(Marker));
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains(Marker));
    }

    [Fact]
    public void A_save_for_a_feature_with_no_topic_of_its_name_is_refused_when_registered()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _calls.Save("nowhere", TestJson.Default.String, (_, _, _) => { }));
    }

    // A settings table in a real writer, the settings topic, and its save: an upsert that publishes the setting. A value
    // starting "refused" fails the save; one starting "crash" throws an exception that holds it. before runs inside the
    // write, first.
    private TempDatabase Settings(Action? before = null)
    {
        var database = new TempDatabase();
        var writer = new AtomicWriter(database.Path, _time, _topics);
        writer.Write("test/setup", scope => scope.Execute("CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)"));
        using (var audit = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            audit.Open();
            using var clear = audit.CreateCommand();
            clear.CommandText = "DELETE FROM audit";
            clear.ExecuteNonQuery();
        }
        _writer.Real = writer;
        _calls.Topic("settings", TestJson.Default.Setting, setting => setting.Key, (_, _) => []);
        _calls.Save("settings", TestJson.Default.String, (key, value, scope) =>
        {
            before?.Invoke();
            if (value.StartsWith("refused", StringComparison.Ordinal))
                throw new CallFailed("refused");
            if (value.StartsWith("crash", StringComparison.Ordinal))
                throw new InvalidOperationException(value);
            scope.Execute("INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value",
                new SqlArg("$key", key), new SqlArg("$value", value));
            scope.Publish("settings", key, new Setting(key, value), TestJson.Default.Setting);
        });
        _disposeAfter.Add(writer);
        return database;
    }

    private readonly List<IDisposable> _disposeAfter = [];

    private static Task SaveAsync(WebSocket socket, long id, string key, string value) =>
        CallAsync(socket, id, "settings", "set", $$"""{"key":"{{key}}","value":{{value}}}""");

    private static List<string> Audit(string path) => Query(path, "SELECT what FROM audit ORDER BY rowid");

    private static string? Value(string path, string key) =>
        Query(path, $"SELECT value FROM settings WHERE key = '{key}'").SingleOrDefault();

    private static List<string> Query(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
