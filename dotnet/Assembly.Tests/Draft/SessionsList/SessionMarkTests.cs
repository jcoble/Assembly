using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Draft.SessionsList;

public sealed class SessionMarkTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly SessionStore _sessions;

    public SessionMarkTests()
    {
        _database = TestDatabase.Open(_file.Path);
        _sessions = SessionStore.Open(_database, _time);
    }

    // Pins: sessions-list#1, sessions-list#2, sessions-list#4, sessions-list#5
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marks_keep_the_first_completion_until_back_to_working(bool remote)
    {
        var session = remote
            ? _sessions.Add("remote-session", "agent", "repo", "workbox-test")
            : _sessions.Create("agent", "repo");
        AssertTimes(null, null);
        Mark("done");
        AssertTimes(1_000_010, null);
        Mark("settled");
        AssertTimes(1_000_010, 1_000_020);
        Mark("done");
        AssertTimes(1_000_010, null);
        Mark("working");
        AssertTimes(null, null);
        Mark("settled");
        AssertTimes(1_000_050, 1_000_050);

        void Mark(string state)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            _database.Writer.Write("test.mark", scope =>
            {
                _sessions.Mark(scope, session.SessionId, state);
                return 0;
            });
        }

        void AssertTimes(long? completed, long? settled)
        {
            var saved = _sessions.Get(session.SessionId)!;
            Assert.Equal((completed, settled), (saved.CompletedAt, saved.SettledAt));
            Assert.Equal(session.UpdatedAt, saved.UpdatedAt);
        }
    }

    // Pins: sessions-list#1, sessions-list#5
    [Fact]
    public void An_older_database_opens_with_both_marks_null()
    {
        _database.Writer.Write("test.older", scope => scope.Execute("""
            DROP TABLE session_info;
            CREATE TABLE session_info(session_id TEXT PRIMARY KEY, agent TEXT NOT NULL, repo_url TEXT NOT NULL,
              title TEXT, acp_session_id TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
            INSERT INTO session_info VALUES ('old', 'agent', 'repo', NULL, NULL, 1, 2);
            """));

        var store = SessionStore.Open(_database, _time);
        var saved = store.Get("old")!;
        Assert.Null(saved.CompletedAt);
        Assert.Null(saved.SettledAt);
        Assert.Equal(saved, SessionStore.Open(_database, _time).Get("old"));
    }

    public void Dispose()
    {
        _database.Dispose();
        _file.Dispose();
    }
}
