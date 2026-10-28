using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Draft.SessionsList;

public sealed class SessionGroupPageTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly SessionStore _sessions;

    public SessionGroupPageTests()
    {
        _database = TestDatabase.Open(_file.Path);
        _sessions = SessionStore.Open(_database, _time);
    }

    // Pins: sessions-list#9
    [Fact]
    public void Open_pages_skip_settled_sessions_and_settled_pages_hold_only_them()
    {
        var made = Enumerable.Range(0, 5).Select(i =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return _sessions.Create("agent", $"repo-{i}").SessionId;
        }).ToArray();
        foreach (var id in new[] { made[1], made[3] })
            _database.Writer.Write("test.mark", scope => { _sessions.Mark(scope, id, "settled"); return 0; });

        Check(false, [made[4], made[2], made[0]]);
        Check(true, [made[3], made[1]]);
        Check(null, made.Reverse().ToArray());

        void Check(bool? settled, string[] expected)
        {
            Assert.Equal(expected, _sessions.List(null, 10, settled: settled).Sessions.Select(item => item.SessionId));
            var seen = new List<string>();
            long? before = null;
            string? beforeId = null;
            do
            {
                var page = _sessions.List(before, 2, beforeId: beforeId, settled: settled);
                seen.AddRange(page.Sessions.Select(item => item.SessionId));
                before = page.NextBefore;
                beforeId = page.NextBeforeId;
            } while (before is not null);
            Assert.Equal(expected, seen);
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        _file.Dispose();
    }
}
