using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitSessionOperationsTests
{
    [Fact]
    public async Task A_project_removed_during_hosted_reads_is_refused_before_the_comment_is_posted()
    {
        await using var f = await Fixture.Create();
        var store = new Assembly.Application.Features.Projects.ProjectStore(f.Db, TimeProvider.System);
        var project = f.Db.Writer.Write("fixture.project", scope => store.AddOrRestore(scope,
            "local", f.Repo.Root, "Project", "github.com/owner/repo").Project);
        var request = f.Request() with
        {
            Session = null,
            ProjectId = project.Id,
            ExpectedRepository = "owner/repo",
            ExpectedCheckoutRoot = f.Repo.Root,
            ExpectedSource = new(f.Machine, 0, null)
        };
        var execute = f.Producer.PrepareProject(request, f.Repo.Root, "owner/repo", () =>
        { if (store.Get(project.Id) is not { RemovedAt: null }) throw new CallFailed("The captured project was removed.", "source_changed"); });
        f.Gh.OnRead = () =>
        {
            f.Gh.OnRead = null;
            f.Db.Writer.Write("fixture.remove", scope => store.Remove(scope, project.Id, false));
        };
        await Assert.ThrowsAsync<CallFailed>(() => execute(default));
        Assert.Equal(3, f.Gh.Reads);
        Assert.Equal(0, f.Gh.Writes);
    }

    [Fact]
    public async Task A_project_pr_action_refuses_a_changed_origin_even_when_checkout_and_head_are_unchanged()
    {
        await using var f = await Fixture.Create();
        await f.Repo.Commit("Initial");
        var head = await f.Repo.Head();
        var request = f.Request() with
        {
            Session = null,
            ProjectId = "project",
            ExpectedRepository = "owner/repo",
            ExpectedCheckoutRoot = f.Repo.Root,
            ExpectedSource = new(f.Machine, 0, null)
        };
        var execute = f.Producer.PrepareProject(request, f.Repo.Root, "owner/repo", () => { });
        await f.Repo.Run("remote", "set-url", "origin", "https://github.com/other/fork");
        Assert.Equal(head, await f.Repo.Head());
        await Assert.ThrowsAsync<CallFailed>(() => execute(default));
        Assert.Equal(0, f.Gh.Reads);
        Assert.Equal(0, f.Gh.Writes);
    }
}
