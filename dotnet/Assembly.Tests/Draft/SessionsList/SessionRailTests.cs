using System.Text.Json;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static Assembly.Application.Features.Chat.Sessions.SessionsFeature;

namespace Assembly.Tests.Draft.SessionsList;

/// <summary>
/// The rail's one SQL read (main-screen spec S1 to S3, PR 1): tab totals over every eligible parent, filters before
/// totals and paging, project groups in a fixed order, started sessions after their starter, and the invalidations that make a
/// window read it again.
/// </summary>
public sealed class SessionRailTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly Recorded _topics = new();
    private readonly AppDatabase _database;
    private readonly SessionStore _sessions;
    private readonly LiveHub _hub;

    public SessionRailTests()
    {
        _database = AppDatabase.Open(_file.Path, _time, _topics);
        var rows = RowStore.Open(_database);
        _sessions = SessionStore.Open(_database, _time, "checkouts");
        _hub = new LiveHub(rows, topics: _topics);
    }

    // Pins: sessions-list#1, sessions-list#6
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartedSessionsFollowTheirStarterAndKeepTheirOwnState(bool remote)
    {
        var starter = Parent(machine: remote ? "workbox-test" : null);
        var other = Parent();
        var started = Helper(starter);
        Running(started);
        Asking(started);
        Assert.Equal([other, starter, started], Collect(next => Read(after: next, limit: 1)));
        Assert.Equal(new RailTotals(3, 1, 1), Read().Totals);
        Assert.False(Entry(Read(), starter).Running);
        Assert.False(Entry(Read(), starter).NeedsYou);
        Assert.Equal(1, Entry(Read(), started).Depth);
        Assert.Equal(started, Read("running").Sessions.Single().SessionId);
        Assert.Equal(started, Read("needs").Sessions.Single().SessionId);
    }

    // Pins: sessions-list#1, sessions-list#6
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSubagentsNeverEnterTheRailOrKeepTheirParentRunning(bool remote)
    {
        var starter = Parent(machine: remote ? "workbox-test" : null);
        var native = _sessions.Child(_sessions.Get(starter)!, "native", "Native subagent").SessionId;
        Running(native);
        Asking(native);
        Rows(native, ("background", RowKind.Task, new { state = "running" }));
        Rows(starter, ("native", RowKind.Subagent, new { state = "running" }),
            (null, RowKind.Turn, new { state = "finished" }));
        Assert.Equal(new RailTotals(1, 0, 0), Read().Totals);
        Assert.Equal(starter, Read().Sessions.Single().SessionId);
        Assert.Empty(Read("running").Sessions);
        Assert.Empty(Read("needs").Sessions);
    }

    [Fact]
    public void CountsIncludeUnloadedParentsOnce()
    {
        var parents = Enumerable.Range(0, 205).Select(_ => Parent()).ToArray();
        for (var i = 0; i < parents.Length; i++)
        {
            if (i % 5 == 0)
                Running(parents[i]);
            if (i % 7 == 0)
                Asking(parents[i]);
        }

        var first = Read(limit: 50);
        Assert.Equal(new RailTotals(205, 41, 30), first.Totals);
        Assert.Equal(50, first.Sessions.Count);
        Assert.Equal(205, Collect(next => Read(after: next, limit: 50)).Count);
        Assert.Equal(41, Read("running", limit: 200).Sessions.Count);
        Assert.Equal(first.Totals, Read("needs", limit: 1).Totals);
    }

    [Fact]
    public void FiltersPrecedeCountsAndPaging()
    {
        var a = Parent("claude");
        var b = Parent("codex");
        Running(b);
        var c = Parent("claude", machine: "workbox-test");
        Asking(c);
        Parent("codex", machine: "workbox-test");
        var settled = Parent("claude");
        Running(settled);
        _database.Writer.Write("test.mark", scope => { _sessions.Mark(scope, settled, "settled"); return 0; });
        // A started session counts under its own provider.
        var mixed = Parent("claude");
        Running(Helper(mixed, "codex"));

        Assert.Equal(new RailTotals(3, 2, 0), Read(providers: ["codex"]).Totals);
        Assert.Equal(new RailTotals(3, 0, 1), Read(providers: ["claude"]).Totals);
        Assert.Equal(new RailTotals(2, 0, 1), Read(locations: ["remote"]).Totals);
        Assert.Equal(new RailTotals(4, 2, 0), Read(providers: ["claude", "codex"], locations: ["local"]).Totals);
        var none = Read(providers: ["nobody"]);
        Assert.Equal(new RailTotals(0, 0, 0), none.Totals);
        Assert.Empty(none.Sessions);
        Assert.Equal(["claude", "codex"], none.Agents);
        Assert.Equal([mixed, c, a], Collect(next => Read(providers: ["claude"], after: next, limit: 2)));
        Assert.DoesNotContain(settled, Read(limit: 200).Sessions.Select(entry => entry.SessionId));
    }

    [Fact]
    public void TitleAndProjectFilterPrecedesCountsAndPaging()
    {
        var titled = Parent(repo: "/r/alpha", title: "Fix the Socket reconnect");
        Running(titled);
        var named = Parent(repo: "/r/socketry");
        var other = Parent(repo: "/r/beta", title: "nothing");
        Asking(other);

        Assert.Equal(new RailTotals(2, 1, 0), Read(filter: "socket").Totals);
        Assert.Equal([titled, named], Collect(next => Read(filter: "SOCKET", after: next, limit: 1)));
        Assert.Equal(new RailTotals(1, 0, 1), Read(filter: "BETA").Totals);
    }

    [Fact]
    public void PagesKeepStableOrderOnTimestampTies()
    {
        var zeta = Parent(repo: "/home/me/Zeta");
        var github = Parent(repo: "https://github.com/o/alpha");
        var mine = Parent(repo: "/home/you/alpha");
        // Five sessions of one repo at the same moment, two of them on another machine: one group, newest id first.
        var tied = new[] { Parent(repo: "/srv/alpha") }
            .Concat(Enumerable.Range(0, 4).Select(i => Parent(repo: "/srv/alpha", machine: i % 2 == 0 ? "workbox-test" : null, tick: false)))
            .ToArray();

        var all = Read(limit: 200).Sessions;
        Assert.Equal([mine, .. tied.OrderDescending(StringComparer.Ordinal), github, zeta], all.Select(entry => entry.SessionId));
        Assert.Equal(["alpha", "alpha", "o/alpha", "Zeta"], all.Where(entry => entry.GroupStart).Select(entry => entry.Group));
        Assert.Equal(all.Select(entry => entry.SessionId), Collect(next => Read(after: next, limit: 2)));

        Assert.Equal("icons/asterisk.svg", Entry(Read(), mine).Icon);
    }

    [Fact]
    public void AReadAgainKeepsTheReadersRowWhereverItMoved()
    {
        // 250 parents of one project, newest first; the reader's row is the 231st, with 130 held above it.
        var parents = Enumerable.Range(0, 250).Select(_ => Parent()).Reverse().ToArray();
        var reader = parents[230];
        var held = Read(anchor: reader, above: 130, limit: 200);
        Assert.Equal((100, reader), (held.Start, held.Sessions[130].SessionId));

        // It moves ahead of every row held before it.
        _time.Advance(TimeSpan.FromSeconds(1));
        _sessions.Touch(reader);
        var moved = Read(anchor: reader, above: 130, limit: 200);
        Assert.Equal((0, reader), (moved.Start, moved.Sessions[0].SessionId));

        // 60 sessions start ahead of it: it stays at the same place in what's read.
        var row = parents[200];
        for (var i = 0; i < 60; i++)
            Parent();
        Assert.Equal(row, Read(anchor: row, above: 149, limit: 200).Sessions[149].SessionId);

        // A row that left the tab: the read falls back to the cursor.
        Assert.Equal(parents[1], Read(anchor: "gone", after: Cursor(Entry(Read(limit: 200), parents[0])), limit: 1).Sessions.Single().SessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackgroundWorkRunsAfterItsTurnEnds(bool remote)
    {
        var session = Parent(machine: remote ? "workbox-test" : null);
        var seq = 0L;
        void Add(string? item, string kind, object payload)
        {
            if (remote)
                Assert.True(_hub.PublishCopy(new StoredRow(session, ++seq, "run-1", item, kind, JsonSerializer.SerializeToElement(payload), 1, PrevSeq: seq - 1)));
            else
                Rows(session, (item, kind, payload));
        }
        Add(null, RowKind.Turn, new { state = "started" });
        Add("t1", RowKind.Task, new { state = "running" });
        Add(null, RowKind.Turn, new { state = "finished" });
        var rail = Read("running");
        Assert.Equal(new RailTotals(1, 1, 0), rail.Totals);
        Assert.True(Entry(rail, session).Running);

        Add("t1", RowKind.Task, new { state = "completed" });
        Assert.Equal(new RailTotals(1, 0, 0), Read("running").Totals);
        Assert.False(Entry(Read(), session).Running);
    }

    [Fact]
    public void ManualWorkingIsNotRunning()
    {
        var idle = Parent();
        var done = Parent();
        Running(done);
        _database.Writer.Write("test.mark", scope => { _sessions.Mark(scope, done, "done"); return 0; });

        var rail = Read();
        Assert.Equal(new RailTotals(2, 1, 0), rail.Totals);
        Assert.False(Entry(rail, idle).Running);
        Assert.Null(Entry(rail, idle).CompletedAt);
        Assert.True(Entry(rail, done).Running);
        Assert.NotNull(Entry(rail, done).CompletedAt);
    }

    [Fact]
    public void UnreadAnswerIsNotAnOpenAsk()
    {
        var answered = Parent();
        Rows(answered, (null, RowKind.Turn, new { state = "started" }), ("q1", RowKind.Ask, new { state = "open" }),
            ("q1", RowKind.Ask, new { state = "answered" }), (null, RowKind.Turn, new { state = "finished" }));

        var rail = Read();
        Assert.Equal(new RailTotals(1, 0, 0), rail.Totals);
        Assert.True(Entry(rail, answered).Unread);
        Assert.False(Entry(rail, answered).NeedsYou);
        Assert.Empty(Read("needs").Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AskTargetRefreshesWhileAnotherAskRemainsOpen(bool remote)
    {
        var session = Parent(machine: remote ? "workbox-test" : null);
        var seq = 0L;
        void Add(string? item, string kind, object payload)
        {
            if (remote)
                Assert.True(_hub.PublishCopy(new StoredRow(session, ++seq, "run-1", item, kind, JsonSerializer.SerializeToElement(payload), 1, PrevSeq: seq - 1)));
            else
                Rows(session, (item, kind, payload));
        }
        Add(null, RowKind.Turn, new { state = "started" });
        Add("a", RowKind.Ask, new { state = "open" });
        Add("b", RowKind.Ask, new { state = "open" });
        Assert.Equal((session, 2L), (Entry(Read(), session).AskSession, Entry(Read(), session).AskSeq));

        _topics.Published.Clear();
        Add("a", RowKind.Ask, new { state = "answered" });

        Assert.Contains((RailTopic, (string?)session, false), _topics.Published.Select(p => (p.Topic, p.Key, p.KeyedOnly)));
        var after = Entry(Read("needs"), session);
        Assert.Equal((session, 3L), (after.AskSession, after.AskSeq));
    }

    [Fact]
    public void HelperChangesReachRailSubscribers()
    {
        var parent = Parent();
        _topics.Published.Clear();
        var helper = Helper(parent);
        Assert.Contains((Topic, (string?)helper, true), _topics.Published.Select(p => (p.Topic, p.Key, p.KeyedOnly)));
        Assert.Contains((RailTopic, (string?)helper, false), _topics.Published.Select(p => (p.Topic, p.Key, p.KeyedOnly)));

        _topics.Published.Clear();
        Asking(helper);
        Assert.Contains((RailTopic, (string?)helper, false), _topics.Published.Select(p => (p.Topic, p.Key, p.KeyedOnly)));
        var rail = Read("needs");
        Assert.Equal(new RailTotals(2, 0, 1), rail.Totals);
        Assert.Equal(helper, Entry(rail, helper).AskSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedBranchComesFromTheLatestStats(bool remote)
    {
        var session = Parent(machine: remote ? "workbox-test" : null);
        Assert.Null(Entry(Read(), session).Branch);
        var seq = 0L;
        void Stats(string? branch, long at)
        {
            var payload = JsonSerializer.SerializeToElement(new { status = "completed", branch });
            if (remote)
                Assert.True(_hub.PublishCopy(new StoredRow(session, ++seq, "run-1", null, RowKind.Stats, payload, at, PrevSeq: seq - 1)));
            else
                _hub.Publish(session, Batch((null, RowKind.Stats, new { status = "completed", branch }), at));
        }
        Stats("main", 10);
        _topics.Published.Clear();
        Stats("lane/rail", 20);
        Assert.Contains((RailTopic, (string?)session, false), _topics.Published.Select(p => (p.Topic, p.Key, p.KeyedOnly)));
        Stats("", 30);
        Stats(null, 40);

        var entry = Entry(Read(), session);
        Assert.Equal(("lane/rail", 20L), (entry.Branch, entry.BranchAt));
    }

    private string Parent(string agent = "claude", string repo = "/home/me/assembly", string? machine = null, string? title = null, bool tick = true)
    {
        if (tick)
            _time.Advance(TimeSpan.FromSeconds(1));
        var id = machine is null
            ? _sessions.Create(agent, repo).SessionId
            : _sessions.Add(Guid.NewGuid().ToString(), agent, repo, machine).SessionId;
        if (title is not null)
            _sessions.SetTitle(id, title, "user");
        return id;
    }

    private string Helper(string parent, string agent = "claude")
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return _sessions.Helper(_sessions.Get(parent)!, agent, "helper").SessionId;
    }

    private void Running(string session) => Rows(session, (null, RowKind.Turn, new { state = "started" }));

    private void Asking(string session) => Rows(session, ("q1", RowKind.Ask, new { state = "open" }));

    private void Rows(string session, params (string? Item, string Kind, object Payload)[] rows) => _hub.Publish(session, Batch(rows));

    private static RowBatch Batch((string? Item, string Kind, object Payload) row, long at) => Batch([row], at);

    private static RowBatch Batch((string? Item, string Kind, object Payload)[] rows, long at = 1)
    {
        var batch = new RowBatch();
        foreach (var (item, kind, payload) in rows)
            batch.Rows.Add(new NewRow("run-1", item, kind, JsonSerializer.SerializeToElement(payload), at));
        return batch;
    }

    private Rail Read(string tab = "all", string[]? providers = null, string[]? locations = null, string? filter = null,
        RailCursor? after = null, int limit = 50, string? anchor = null, int above = 0) =>
        _sessions.Rail(new RailArgs(tab, providers, locations, filter, after, limit, anchor, above));

    private static RailCursor Cursor(RailEntry entry) => new(entry.Group, entry.RepoUrl, entry.Ordering);

    /// <summary>Every parent, following each page's cursor from the first.</summary>
    private static List<string> Collect(Func<RailCursor?, Rail> read)
    {
        var seen = new List<string>();
        RailCursor? next = null;
        do
        {
            var page = read(next);
            seen.AddRange(page.Sessions.Select(entry => entry.SessionId));
            next = page.Next;
        } while (next is not null);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        return seen;
    }

    private static RailEntry Entry(Rail rail, string session) => rail.Sessions.Single(entry => entry.SessionId == session);

    public void Dispose()
    {
        _hub.Dispose();
        _database.Dispose();
        _file.Dispose();
    }

    private sealed class Recorded : ITopics
    {
        public List<(string Topic, string? Key, bool KeyedOnly)> Published { get; } = [];

        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            lock (Published)
                Published.Add((topic, key, keyedOnly));
        }

        public bool HasSubscribers(string topic, string key) => true;

        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
}
