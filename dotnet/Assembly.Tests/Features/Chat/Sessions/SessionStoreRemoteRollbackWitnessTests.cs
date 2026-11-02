using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

/// <summary>The same original-public-API rollback witness runs against the immutable baseline and the repaired source.</summary>
public sealed class SessionStoreRemoteRollbackWitnessTests
{
    [Fact]
    public void Original_public_remote_apply_does_not_expose_a_summary_from_a_failed_commit()
    {
        using var file = new TempDatabase();
        var topics = new RecordingTopics();
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        using var db = AppDatabase.Open(file.Path, time, topics);
        var summaries = SessionSummaries.Load(RowStore.Open(db));
        var store = SessionStore.Open(db, time, "checkouts", summaries);
        store.Add("copied", "claude", "repo", "workbox-test");
        store.SetTitle("copied", "old", "prompt");
        var before = store.Get("copied")!;
        topics.Sent.Clear();
        db.Writer.Write("test.deferred", scope => scope.Execute("""
            CREATE TABLE parent_fault(id INTEGER PRIMARY KEY);
            CREATE TABLE child_fault(id INTEGER REFERENCES parent_fault(id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fail_remote AFTER UPDATE OF updated_at ON session_info BEGIN INSERT INTO child_fault VALUES(42); END;
            """));
        var incoming = store.Item(before) with
        {
            Title = "new\0😀",
            TitleSource = "helper",
            UpdatedAt = 2000,
            State = "running",
            ActiveRunId = "run",
            PendingAsk = true,
            Background = 3
        };

        Assert.Throws<SqliteException>(() => store.ApplyRemote(incoming));

        // Check actual database rollback and no publication BEFORE testing the baseline's leaked memory summary.
        Assert.Equal(before, store.Get("copied"));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM child_fault", row => row.Long(0), default));
        Assert.Empty(topics.Sent);
        Assert.Equal(SessionSummary.None, summaries.Get("copied"));
    }

    private sealed class RecordingTopics : ITopics
    {
        public List<string> Sent { get; } = [];
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
        public bool HasSubscribers(string topic, string key) => true;
        public void Publish(string topic, string? key, string json, bool keyedOnly = false) => Sent.Add(json);
    }
}
