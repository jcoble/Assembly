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
        _sessions = SessionStore.Open(_database, _time, "checkouts");
    }
    // Pins: sessions-list#7
    [Fact]
    public void StatusFilterKeepsOnlyThePickedLifecycles()
    {
        var working = Parent();
        var done = Parent();
        var settled = Parent();
        Mark(done, "done");
        Mark(settled, "settled");
        Assert.Equal([working], Sessions(Read(statuses: ["working"])));
        Assert.Equal([done], Sessions(Read(statuses: ["done"])));
        Assert.Equal([settled], Sessions(Read(statuses: ["settled"])));
        Assert.Equal(3, Sessions(Read()).Count);
    }
    // Pins: sessions-list#1, sessions-list#9
    [Fact]
    public void SectionCountsFollowTheFiltersBeforePaging()
    {
        Parent("claude");
        Parent("claude");
        Parent("codex");
        var done = Parent("claude");
        Mark(done, "done");
        var page = Read(providers: ["claude"], groupBy: new(true, false), limit: 1);
        var header = Assert.Single(page.Sessions);
        Assert.Equal(("section", "working", 2), (header.Kind, header.Section, header.Count));
    }
    // Pins: sessions-list#6, sessions-list#9
    [Fact]
    public void ProjectGroupsSpanMachinesAndListEach()
    {
        var local = Parent(repo: "/work/assembly");
        var remote = Parent(repo: "/work/assembly", machine: "workbox-test");
        var rail = Read();
        var header = Assert.Single(rail.Sessions, entry => entry.Kind == "project");
        Assert.Equal(("assembly", 2), (header.Group, header.Count));
        Assert.Equal(["local", "workbox-test"], header.Machines.Order().ToArray());
        Assert.Equal([remote, local], Sessions(rail));
    }
    // Pins: sessions-list#6
    [Fact]
    public void GroupByNoneIsOneFlatList()
    {
        var older = Parent(repo: "/work/zeta");
        var newer = Parent(repo: "/work/alpha");
        var rail = Read(groupBy: new(false, false));
        Assert.All(rail.Sessions, entry => Assert.Equal("session", entry.Kind));
        Assert.Equal([newer, older], Sessions(rail));
    }
    // Pins: sessions-list#6
    [Theory]
    [InlineData("asc", "Alpha", "Zulu")]
    [InlineData("desc", "Zulu", "Alpha")]
    public void SortByNameOrdersGroupsAndRows(string dir, string first, string second)
    {
        Parent(repo: "/work/zeta", title: "Middle");
        Parent(repo: "/work/alpha", title: second);
        Parent(repo: "/work/alpha", title: first);
        var rows = Read(groupBy: new(false, true), sort: "name", dir: dir).Sessions;
        Assert.Equal(["alpha", "zeta"], rows.Where(entry => entry.Kind == "project").Select(entry => entry.Group));
        Assert.Equal(
            dir == "asc" ? ["Alpha", "Zulu"] : ["Zulu", "Alpha"],
            rows.Where(entry => entry.Kind == "session" && entry.RepoUrl == "/work/alpha").Select(entry => entry.Title));
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
    public void AFoldedSectionReturnsItsHeaderAndCountButNoRows()
    {
        Parent();
        var settled = Parent();
        Mark(settled, "settled");
        var rail = Read(groupBy: new(true, true), folded: ["settled"]);
        var header = Assert.Single(rail.Sessions, entry => entry.Kind == "section" && entry.Section == "settled");
        Assert.Equal(1, header.Count);
        Assert.DoesNotContain(rail.Sessions, entry => entry.Kind == "project" && entry.Section == "settled");
        Assert.DoesNotContain(rail.Sessions, entry => entry.Kind == "session" && entry.Section == "settled");
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
