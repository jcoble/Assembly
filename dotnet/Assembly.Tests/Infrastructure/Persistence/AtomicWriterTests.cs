using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Persistence;

/// <summary>The writer's audit row, its publishes and its prunes (plan 5.6, B3), on a real temporary SQLite file.</summary>
public sealed class AtomicWriterTests : IDisposable
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
    private readonly FakeTopics _topics = new();
    private AppDatabase _db;

    public AtomicWriterTests()
    {
        _db = Open();
        _db.Writer.Write("test.schema", scope => scope.Execute("CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT);"));
        _topics.Sent.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public void A_write_whose_second_statement_throws_leaves_nothing_and_publishes_nothing()
    {
        Assert.ThrowsAny<SqliteException>(() => _db.Writer.Write("test.fail", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            scope.Publish("notes", "1", "a", NoteJson);
            return scope.Execute("INSERT INTO missing(id) VALUES (1);");
        }));

        Assert.Equal(0, Count("notes"));
        Assert.Equal(0, Count("audit WHERE what = 'test.fail'"));
        Assert.Empty(_topics.Sent);
    }

    [Fact]
    public void A_write_started_inside_a_write_on_the_same_thread_throws_and_the_outer_one_leaves_nothing()
    {
        // The lock lets the same thread back in, so it's the transaction that stops it, with an error, never a hang.
        Assert.Throws<InvalidOperationException>(() => _db.Writer.Write("test.outer", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            return _db.Writer.Write("test.inner", inner => inner.Execute("INSERT INTO notes(id, text) VALUES (2, 'b');"));
        }));

        Assert.Equal(0, Count("notes"));
        Assert.Empty(_topics.Sent);
    }

    [Fact]
    public void A_write_leaves_its_rows_one_audit_row_and_its_publish_sent_after_commit()
    {
        // The fake target reads the file on its own connection: the row is there by the time the publish arrives.
        _topics.OnPublish = () => Assert.Equal(1, Count("notes"));

        _db.Writer.Write("notes/add", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            scope.Publish("notes", "1", "a", NoteJson);
            return 0;
        });

        Assert.Equal(1, Count("notes"));
        Assert.Equal(["test.schema", "notes/add"], Audit());
        Assert.Equal([("notes", "1", "\"a\"")], _topics.Sent);
    }

    [Fact]
    public void A_failing_commit_leaves_nothing_and_publishes_nothing()
    {
        // A deferred foreign key the write breaks fails at commit, not at the insert.
        _db.Writer.Write("test.schema", scope => scope.Execute(
            "CREATE TABLE parents(id INTEGER PRIMARY KEY); CREATE TABLE children(id INTEGER PRIMARY KEY, parent INTEGER REFERENCES parents(id) DEFERRABLE INITIALLY DEFERRED);"));

        Assert.ThrowsAny<SqliteException>(() => _db.Writer.Write("test.orphan", scope =>
        {
            scope.Execute("INSERT INTO children(id, parent) VALUES (1, 99);");
            scope.Publish("children", "1", "orphan", NoteJson);
            return 0;
        }));

        Assert.Equal(0, Count("children"));
        Assert.Equal(0, Count("audit WHERE what = 'test.orphan'"));
        Assert.Empty(_topics.Sent);
    }

    [Fact]
    public void A_chat_row_batch_and_its_one_audit_row_commit_together()
    {
        var rows = RowStore.Open(_db);
        var batch = new RowBatch();
        batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonDocument.Parse("""{"text":"x"}""").RootElement, 1));
        batch.Rows.Add(new NewRow("run-1", null, RowKind.User, JsonDocument.Parse("""{"text":"y"}""").RootElement, 1));

        rows.Commit("s1", batch);

        Assert.Equal(2, Count("rows"));
        Assert.Equal(1, Count("audit WHERE what = 'rows.commit'"));
    }

    [Fact]
    public void Opening_prunes_audit_rows_older_than_30_days()
    {
        AddAudit(_time.GetUtcNow() - 31 * Day, 2);
        AddAudit(_time.GetUtcNow() - 29 * Day, 3);

        Reopen();

        Assert.Equal(3, Count("audit WHERE what = 'old'"));
    }

    [Fact]
    public void A_write_a_day_after_the_last_prune_prunes_and_a_second_write_that_day_does_not()
    {
        _time.Advance(31 * Day);
        AddAudit(_time.GetUtcNow() - 31 * Day, 4);
        // The write commits and publishes before the prune runs: its publish still finds the expired rows.
        _topics.OnPublish = () => Assert.Equal(4, Count("audit WHERE what = 'old'"));

        _db.Writer.Write("notes/add", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            scope.Publish("notes", "1", "a", NoteJson);
            return 0;
        });
        Assert.Single(_topics.Sent);
        Assert.Equal(0, Count("audit WHERE what = 'old'"));
        _topics.OnPublish = null;

        AddAudit(_time.GetUtcNow() - 31 * Day, 4);
        _time.Advance(TimeSpan.FromHours(23));
        Note(2);
        Assert.Equal(4, Count("audit WHERE what = 'old'"));
    }

    [Fact]
    public void The_triggering_write_commits_and_publishes_first_and_a_failed_prune_leaves_it_committed()
    {
        _db.Writer.Write("test.schema", scope => scope.Execute(
            "CREATE TRIGGER no_prune BEFORE DELETE ON audit BEGIN SELECT RAISE(ABORT, 'no prune'); END;"));
        _time.Advance(2 * Day);
        AddAudit(_time.GetUtcNow() - 31 * Day, 1);
        _topics.OnPublish = () => Assert.Equal(1, Count("audit WHERE what = 'old'"));

        _db.Writer.Write("notes/add", scope =>
        {
            scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
            scope.Publish("notes", "1", "a", NoteJson);
            return 0;
        });

        Assert.Equal(1, Count("notes"));
        Assert.Single(_topics.Sent);
        Assert.Equal(1, Count("audit WHERE what = 'old'"));
    }

    [Fact]
    public void A_backlog_over_the_cap_clears_over_the_following_writes()
    {
        _time.Advance(2 * Day);
        AddAudit(_time.GetUtcNow() - 31 * Day, 12_000);

        Note(1);
        Assert.Equal(7_000, Count("audit WHERE what = 'old'"));
        Note(2);
        Assert.Equal(2_000, Count("audit WHERE what = 'old'"));
        Note(3);
        Assert.Equal(0, Count("audit WHERE what = 'old'"));

        AddAudit(_time.GetUtcNow() - 31 * Day, 1);
        Note(4);
        Assert.Equal(1, Count("audit WHERE what = 'old'"));
    }

    [Fact]
    public void An_async_body_throws_and_rolls_back()
    {
        // The writer throws before it returns the task, so this records the throw itself, not a faulted task.
        var error = Record.Exception(() =>
        {
            _db.Writer.Write("test.async", async scope =>
            {
                scope.Execute("INSERT INTO notes(id, text) VALUES (1, 'a');");
                await Task.CompletedTask;
            });
        });

        Assert.IsType<InvalidOperationException>(error);

        Assert.Equal(0, Count("notes"));
        Assert.Equal(0, Count("audit WHERE what = 'test.async'"));
    }

    private static JsonTypeInfo<string> NoteJson => (JsonTypeInfo<string>)JsonSerializerOptions.Default.GetTypeInfo(typeof(string));

    private AppDatabase Open() => AppDatabase.Open(_file.Path, _time, _topics);

    private void Reopen()
    {
        _db.Dispose();
        _db = Open();
    }

    private void Note(int id) =>
        _db.Writer.Write("notes/add", scope => scope.Execute("INSERT INTO notes(id, text) VALUES ($id, 'n');", new SqlArg("$id", (long)id)));

    // Audit rows as an older day left them, written behind the writer's back.
    private void AddAudit(DateTimeOffset at, int count) =>
        Run("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < $count) INSERT INTO audit(at, what) SELECT $at, 'old' FROM n;",
            ("$count", count), ("$at", at.ToUnixTimeMilliseconds()));

    private List<string> Audit() =>
        _db.Query("SELECT what FROM audit ORDER BY rowid;", row => row.Text(0), CancellationToken.None);

    private long Count(string from)
    {
        using var db = new SqliteConnection($"Data Source={_file.Path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {from};";
        return (long)command.ExecuteScalar()!;
    }

    private void Run(string sql, params (string Name, object Value)[] args)
    {
        using var db = new SqliteConnection($"Data Source={_file.Path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private sealed class FakeTopics : ITopics
    {
        public List<(string Topic, string? Key, string Json)> Sent { get; } = [];

        public Action? OnPublish { get; set; }

        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            OnPublish?.Invoke();
            Sent.Add((topic, key, json));
        }

        public bool HasSubscribers(string topic, string key) => true;
    }
}
