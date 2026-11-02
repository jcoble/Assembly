using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Projects;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    private readonly TopicSet _topics = new();
    private readonly AppDatabase _db;
    private readonly ProjectStore _store;
    public ProjectStoreTests()
    {
        _db = AppDatabase.Open(_file.Path, _time, _topics);
        _store = new(_db, _time);
    }
    public void Dispose() { _db.Dispose(); _file.Dispose(); }
    private T Write<T>(Func<IWriteScope, T> action) => _db.Writer.Write("projects.test", action);
    private (ProjectRecord Project, bool AlreadyExisted) Add(string path, string machine = "local") =>
        Write(s => _store.AddOrRestore(s, machine, path, Path.GetFileName(path), null));

    [Fact]
    public void Adding_the_same_root_twice_returns_the_first()
    {
        var a = Add("/w/app"); var b = Add("/w/app");
        Assert.False(a.AlreadyExisted); Assert.True(b.AlreadyExisted); Assert.Equal(a.Project, b.Project);
        Assert.Single(_store.List(null, null, null, 100, default).Projects);
        Assert.Equal(1, _store.Revision(default));
    }
    [Fact]
    public void The_same_path_on_two_machines_is_two_projects() =>
        Assert.NotEqual(Add("/w/app").Project.Id, Add("/w/app", "workbox-test").Project.Id);
    [Fact]
    public void List_puts_pinned_first_then_recent_use_then_creation_and_pages_without_repeats()
    {
        var a = Add("/w/never-a").Project; _time.Advance(TimeSpan.FromSeconds(1));
        var b = Add("/w/never-b").Project;
        var early = Add("/w/used-early").Project; var late = Add("/w/used-late").Project; var pin = Add("/w/pinned").Project;
        Write(s => { _store.Touch(s, early.Id); return 0; }); _time.Advance(TimeSpan.FromSeconds(1));
        Write(s => { _store.Touch(s, late.Id); return _store.Pin(s, pin.Id, true); });
        var all = new List<ProjectRecord>(); ProjectCursor? cursor = null;
        do { var p = _store.List(null, null, cursor, 2, default); all.AddRange(p.Projects); cursor = p.Next; } while (cursor is not null);
        Assert.Equal([pin.Id, late.Id, early.Id, a.Id, b.Id], all.Select(p => p.Id));
        Assert.Equal(5, all.Select(p => p.Id).Distinct().Count());
    }
    [Fact]
    public void Filter_and_machine_are_applied_before_the_page()
    {
        Add("/w/Alpha"); Add("/w/Alpha", "workbox-test"); Add("/w/beta");
        Assert.Single(_store.List("ALP", "workbox-test", null, 1, default).Projects);
        Assert.Empty(_store.List("absent", null, null, 100, default).Projects);
    }
    [Fact]
    public void Pin_keeps_its_time_while_pinned_and_a_later_pin_is_new()
    {
        var p = Add("/w/app").Project;
        var first = Write(s => _store.Pin(s, p.Id, true)).PinnedAt;
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(first, Write(s => _store.Pin(s, p.Id, true)).PinnedAt);
        Assert.Null(Write(s => _store.Pin(s, p.Id, false)).PinnedAt);
        Assert.True(Write(s => _store.Pin(s, p.Id, true)).PinnedAt > first);
    }
    [Fact]
    public void Rename_trims_and_refuses_empty_and_gone_without_changing_identity()
    {
        var p = Add("/w/app").Project;
        var renamed = Write(s => _store.Rename(s, p.Id, "  New  "));
        Assert.Equal(p with { Title = "New" }, renamed);
        Assert.Throws<CallBadArguments>(() => Write(s => _store.Rename(s, p.Id, "  ")));
        Assert.Throws<KeyNotFoundException>(() => Write(s => _store.Rename(s, "missing", "x")));
        Assert.Equal(renamed, _store.Get(p.Id));
    }
    [Fact]
    public void A_failed_write_rolls_back_data_revision_and_publication()
    {
        var p = Add("/w/app").Project; var revisions = new List<string>();
        using var subscription = _topics.Subscribe("projects", null, _ => { }, (_, json) => revisions.Add(json));
        Assert.Throws<InvalidOperationException>(() => Write<int>(s =>
        {
            _store.Rename(s, p.Id, "changed");
            _store.Pin(s, p.Id, true);
            throw new InvalidOperationException("injected before commit");
        }));
        Assert.Equal(p, _store.Get(p.Id)); Assert.Equal(1, _store.Revision(default)); Assert.Empty(revisions);
    }
    [Fact]
    public void Remove_and_readd_retain_identity_title_and_metadata()
    {
        var p = Add("/w/app").Project;
        var removed = Write(s => _store.Remove(s, p.Id, false));
        Assert.NotNull(removed.RemovedAt); Assert.Empty(_store.List(null, null, null, 100, default).Projects);
        Assert.Equal(removed, Write(s => _store.Remove(s, p.Id, false)));
        Assert.Throws<KeyNotFoundException>(() => Write(s => _store.Rename(s, p.Id, "x")));
        Assert.Throws<KeyNotFoundException>(() => Write(s => _store.Pin(s, p.Id, true)));
        Assert.Throws<KeyNotFoundException>(() => Write(s => _store.SetRoot(s, p.Id, new("/new", "new", null, false, null, false))));
        var restored = Write(s => _store.AddOrRestore(s, "local", p.RootPath, "replacement", "host/o/r"));
        Assert.True(restored.AlreadyExisted);
        Assert.Equal(p with { RepoKey = "host/o/r" }, restored.Project);
    }
    [Fact]
    public void SetRoot_preserves_identity_and_refuses_even_a_removed_duplicate()
    {
        var a = Add("/w/a").Project; var b = Add("/w/b").Project;
        Write(s => _store.Remove(s, b.Id, false));
        Assert.Throws<CallFailed>(() => Write(s => _store.SetRoot(s, a.Id, new(b.RootPath, "b", null, false, null, false))));
        Assert.Equal(a, _store.Get(a.Id));
        var changed = Write(s => _store.SetRoot(s, a.Id, new("/w/c", "c", "host/o/c", true, "/w/c/.git", false)));
        Assert.Equal(a with { RootPath = "/w/c", RepoKey = "host/o/c" }, changed.Project);
    }
    [Fact]
    public void Removal_survives_reopening_the_database()
    {
        var p = Add("/w/app").Project; var removed = Write(s => _store.Remove(s, p.Id, false));
        using var other = AppDatabase.Open(_file.Path, _time, new TopicSet());
        var reopened = new ProjectStore(other, _time);
        Assert.Equal(removed, reopened.Get(p.Id)); Assert.Empty(reopened.List(null, null, null, 100, default).Projects);
    }
}
