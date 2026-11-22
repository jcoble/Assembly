using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static Assembly.Application.Features.Chat.Sessions.SessionsFeature;

namespace Assembly.Tests.Draft.SessionsList;

public sealed class SessionRailGroupTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AppDatabase _database;
    private readonly SessionStore _sessions;

    public SessionRailGroupTests()
    {
        _database = AppDatabase.Open(_file.Path, _time, new SilentTopics());
        RowStore.Open(_database);
        global::Assembly.Engine.Persistence.EngineSchema.Open(_database, _time.GetUtcNow().ToUnixTimeMilliseconds());
        _sessions = SessionStore.Open(_database, _time, "checkouts");
    }
    // Pins: sessions-list#6
    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public void SortByNameUsesTheShownName(string dir)
    {
        var alpha = Parent(repo: "/work/x", title: "Alpha");
        var untitled = Parent(repo: "/work/middle");
        var zulu = Parent(repo: "/work/y", title: "Zulu");
        string[] ascending = [alpha, untitled, zulu];
        Assert.Equal(dir == "asc" ? ascending : [.. ascending.Reverse()],
            Sessions(Read(groupBy: new(false, false), sort: "name", dir: dir)));
    }
    // Pins: sessions-list#6, sessions-list#9
    [Fact]
    public void PagesStayStableAcrossGroupsOnTies()
    {
        for (var i = 0; i < 3; i++)
            Parent(repo: "/work/alpha", tick: false);
        for (var i = 0; i < 3; i++)
            Parent(repo: "/work/zeta", tick: false);

        var all = Read(limit: 200).Sessions.Select(entry => entry.SessionId).ToArray();
        var paged = Collect(after => Read(after: after, limit: 2));
        Assert.Equal(all, paged);
        Assert.Equal(paged.Length, paged.Distinct().Count());
    }
    // Pins: sessions-list#9
    [Fact]
    public void FoldingFromItsHeaderReturnsTheHeaderFirst()
    {
        Parent(repo: "/work/alpha");
        Parent(repo: "/work/zeta");
        var rail = Read(folded: ["::/work/zeta"], anchor: "project:::/work/zeta");
        var header = rail.Sessions[0];
        Assert.Equal(("project", "/work/zeta", 1), (header.Kind, header.RepoUrl, header.Count));
        Assert.Single(rail.Sessions);
    }
    private string Parent(string agent = "claude", string repo = "/work/assembly", string? machine = null,
        string? title = null, bool tick = true)
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
    private void Mark(string session, string state) =>
        _database.Writer.Write("test.mark", scope => { _sessions.Mark(scope, session, state); return 0; });
    private Rail Read(string[]? providers = null, string[]? statuses = null, RailGroupBy? groupBy = null,
        string sort = "recent", string dir = "desc", string[]? folded = null, RailCursor? after = null, int limit = 50,
        string? anchor = null) =>
        _sessions.Rail(new RailArgs(Providers: providers, Statuses: statuses, GroupBy: groupBy ?? new(false, true),
            Sort: sort, Dir: dir, Folded: folded, After: after, Limit: limit, Anchor: anchor));
    private static List<string> Sessions(Rail rail) =>
        [.. rail.Sessions.Where(entry => entry.Kind == "session").Select(entry => entry.SessionId)];
    private static string[] Collect(Func<RailCursor?, Rail> read)
    {
        var seen = new List<string>();
        RailCursor? next = null;
        do
        {
            var page = read(next);
            seen.AddRange(page.Sessions.Select(entry => entry.SessionId));
            next = page.Next;
        } while (next is not null);
        return [.. seen];
    }
    public void Dispose()
    {
        _database.Dispose();
        _file.Dispose();
    }
    private sealed class SilentTopics : ITopics
    {
        public void Publish(string topic, string? key, string json, bool keyedOnly = false) { }
        public bool HasSubscribers(string topic, string key) => false;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
}
