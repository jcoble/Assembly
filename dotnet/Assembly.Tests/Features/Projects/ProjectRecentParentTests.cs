using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Projects;

public sealed class ProjectRecentParentTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    private readonly AppDatabase _db;
    private readonly ProjectStore _store;
    public ProjectRecentParentTests()
    {
        _db = AppDatabase.Open(_file.Path, _time, new TopicSet());
        _store = new(_db, _time);
    }
    public void Dispose() { _db.Dispose(); _file.Dispose(); }
    private ProjectRecord Add(string machine, string path, bool used = true) => _db.Writer.Write("recent-parent.test", scope =>
    {
        var project = _store.AddOrRestore(scope, machine, path, Path.GetFileName(path), null).Project;
        if (used) _store.Touch(scope, project.Id);
        return project;
    });

    [Fact]
    public void Most_recent_used_live_project_on_exact_machine_supplies_its_parent()
    {
        var pinned = Add("local", "/older/pinned");
        _db.Writer.Write("recent-parent.pin", scope => _store.Pin(scope, pinned.Id, true));
        _time.Advance(TimeSpan.FromSeconds(1));
        Add("local", "/newer/project");
        _time.Advance(TimeSpan.FromSeconds(1));
        Add("remote", "/elsewhere/project");
        Add("local", "/never-used/project", false);
        var removed = Add("local", "/removed/project");
        _db.Writer.Write("recent-parent.remove", scope => _store.Remove(scope, removed.Id, false));
        Assert.Equal("/newer", _store.RecentParent("local", default));
        Assert.Equal("/elsewhere", _store.RecentParent("remote", default));
        Assert.Null(_store.RecentParent("missing", default));
    }

    [Fact]
    public void No_used_project_or_a_filesystem_root_does_not_invent_a_parent()
    {
        Add("unused", "/unused/project", false);
        Add("root", Path.GetPathRoot(Path.GetTempPath())!);
        Assert.Null(_store.RecentParent("unused", default));
        Assert.Null(_store.RecentParent("root", default));
    }
}
