using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Chat;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    private static object Status(string view) => new { session = view };

    [Fact]
    public async Task A_viewed_folder_reads_and_changes_with_no_session_there()
    {
        await using var f = await Fixture.Start();
        f.Projects["p1"] = ("local", f.Root);
        using var client = await f.Open();
        var view = FolderView.Id("p1", f.Linked);
        var status = (await f.Call(client, "status", Status(view))).GetProperty("result");
        Assert.Equal(f.Linked, status.GetProperty("checkoutRoot").GetString());
        var staged = await f.Call(client, "stage", new { session = view, paths = new[] { "tracked.txt" }, expectedCheckoutRoot = f.Linked });
        Assert.Equal("result", staged.GetProperty("t").GetString());
        Assert.Contains("tracked.txt", (await Fixture.Run(f.Linked, "diff", "--cached", "--name-only")));
    }

    [Fact]
    public async Task A_folder_a_running_session_works_in_reads_but_refuses_changes_and_names_the_session()
    {
        await using var f = await Fixture.Start();
        f.Projects["p1"] = ("local", f.Root);
        f.Owner.Fixture.Db.Writer.Write("fixture.running", scope => scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,folder,title,state,activity_state,holder,epoch,cancel_requested)
            SELECT $id,0,machine_id,0,0,'claude',$folder,'Busy session','running','running','local',0,0 FROM engine_meta WHERE singleton=1
            """, new SqlArg("$id", f.Session), new SqlArg("$folder", f.Root)));
        using var client = await f.Open();
        var view = FolderView.Id("p1", f.Root);
        Assert.Equal("result", (await f.Call(client, "status", Status(view))).GetProperty("t").GetString());
        var refused = await f.Call(client, "stage", new { session = view, paths = new[] { "tracked.txt" }, expectedCheckoutRoot = f.Root });
        AssertCode(refused, "folder_in_use");
        Assert.Contains("\"Busy session\" is working in this folder", refused.GetProperty("message").GetString());
        Assert.Equal("", await Fixture.Run(f.Root, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task A_remote_project_is_read_and_changed_on_its_own_machine()
    {
        await using var f = await Fixture.Start(remote: true);
        f.Projects["p1"] = ("workbox-test", f.Root);
        using var client = await f.Open();
        var view = FolderView.Id("p1", f.Linked);
        var status = (await f.Call(client, "status", Status(view))).GetProperty("result");
        Assert.Equal(f.Linked, status.GetProperty("checkoutRoot").GetString());
        Assert.True(f.Git.Count > 0, "the remote's git answered");
        var staged = await f.Call(client, "stage", new { session = view, paths = new[] { "tracked.txt" }, expectedCheckoutRoot = f.Linked });
        Assert.Equal("result", staged.GetProperty("t").GetString());
    }

    [Fact]
    public async Task Only_a_viewed_folder_switches_branch_here()
    {
        await using var f = await Fixture.Start();
        f.Projects["p1"] = ("local", f.Root);
        await Fixture.Run(f.Root, "branch", "other");
        using var client = await f.Open();
        var view = FolderView.Id("p1", f.Root);
        var switched = await f.Call(client, "switchBranch", new { session = view, name = "other", expectedCheckoutRoot = f.Root });
        Assert.Equal("result", switched.GetProperty("t").GetString());
        Assert.Equal("other", (await Fixture.Run(f.Root, "branch", "--show-current")).Trim());
        var own = await f.Call(client, "switchBranch", new { session = f.Session, name = "main", expectedCheckoutRoot = f.Root, expectedSource = f.Source });
        Assert.Equal("error", own.GetProperty("t").GetString());
        Assert.Equal("other", (await Fixture.Run(f.Root, "branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task A_view_of_a_folder_that_is_not_whole_or_not_there_reads_nothing()
    {
        await using var f = await Fixture.Start();
        using var client = await f.Open();
        Assert.Equal("error", (await f.Call(client, "status", Status(FolderView.Id("p1", "relative/path")))).GetProperty("t").GetString());
        AssertCode(await f.Call(client, "status", Status(FolderView.Id("p1", Path.Combine(f.Home, "absent")))), "checkout_unavailable");
    }
}
