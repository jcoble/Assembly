using Assembly.Application.Common.Calls;
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

    [Theory]
    [InlineData("stage")]
    [InlineData("unstage")]
    [InlineData("discard")]
    [InlineData("commit")]
    [InlineData("createBranch")]
    [InlineData("switchBranch")]
    [InlineData("stash")]
    [InlineData("applyStash")]
    [InlineData("dropStash")]
    [InlineData("addRemote")]
    [InlineData("init")]
    public async Task Every_git_write_queued_on_a_viewed_folder_is_refused_when_a_session_starts_there_first(string method)
    {
        await using var f = await Fixture.Start();
        f.Projects["p1"] = ("local", f.Linked);
        using var client = await f.Open();
        var root = Path.GetFullPath(f.Linked);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = f.LocalWrites.RunAsync(root, () => { entered.SetResult(); return release.Task; });
        await entered.Task;
        var before = await Fixture.Run(f.Linked, "status", "--porcelain=v2", "--branch");
        var args = new Dictionary<string, object?>
        {
            ["session"] = FolderView.Id("p1", f.Linked),
            ["expectedCheckoutRoot"] = f.Linked,
            ["paths"] = new[] { "tracked.txt" },
            ["tracked"] = new[] { "tracked.txt" },
            ["untracked"] = Array.Empty<string>(),
            ["expected"] = Array.Empty<object>(),
            ["message"] = "a message",
            ["includeUntracked"] = false,
            ["amend"] = false,
            ["name"] = "other",
            ["url"] = "https://example.invalid/r.git",
            ["sha"] = f.Sha,
        };
        var stage = f.Call(client, method, args);
        await WaitUntil(() => f.LocalWrites.UsersOf(root) == 2);
        f.AddEngineSession(f.Linked, "Busy session", state: "running", activity: "running");
        release.SetResult(0);
        await holder;
        AssertCode(await stage, "folder_in_use");
        Assert.Equal(before, await Fixture.Run(f.Linked, "status", "--porcelain=v2", "--branch"));
    }

    [Fact]
    public async Task A_git_operation_on_a_viewed_folder_is_refused_when_a_session_starts_there_before_its_turn()
    {
        await using var f = await Fixture.Start();
        var run = f.Operation(FolderView.Id("p1", f.Linked), f.Linked);
        f.AddEngineSession(f.Linked, "Busy session", state: "running", activity: "running");
        var refused = await Assert.ThrowsAsync<CallFailed>(() => run(CancellationToken.None));
        Assert.Equal("folder_in_use", refused.Reason);
    }

    [Fact]
    public async Task A_session_is_seen_in_a_folder_reached_through_a_symlink_on_either_side()
    {
        await using var f = await Fixture.Start();
        var link = Path.Combine(f.Home, "link-to-linked");
        Directory.CreateSymbolicLink(link, f.Linked);
        var views = new FolderViews(f.Hosts.Mac.Fixture.Db, null);
        f.AddEngineSession(f.Linked, "Busy session", state: "running", activity: "running");
        Assert.Equal("Busy session", views.WorkingIn(link, default));
        Assert.Equal("Busy session", views.WorkingIn(Path.Combine(link, "inner"), default));

        var other = Directory.CreateDirectory(Path.Combine(f.Home, "other")).FullName;
        var otherLink = Path.Combine(f.Home, "link-to-other");
        Directory.CreateSymbolicLink(otherLink, other);
        Assert.Null(views.WorkingIn(other, default));
        f.AddEngineSession(otherLink, "Linked session", state: "needs_me", activity: "idle");
        Assert.Equal("Linked session", views.WorkingIn(other, default));
    }

    [Fact]
    public async Task A_case_insensitive_disk_sees_a_session_in_a_differently_cased_path()
    {
        await using var f = await Fixture.Start();
        var db = f.Hosts.Mac.Fixture.Db;
        f.AddEngineSession(f.Linked, "Busy session", state: "running", activity: "running");
        var cased = f.Linked.ToUpperInvariant();
        Assert.Equal("Busy session", new FolderViews(db, null, StringComparison.OrdinalIgnoreCase).WorkingIn(cased, default));
        Assert.Null(new FolderViews(db, null, StringComparison.Ordinal).WorkingIn(cased, default));
    }

    [Theory]
    [InlineData("/w/Repo", "/w/repo", true)]
    [InlineData("/w/Repo/src", "/w/repo", true)]
    [InlineData("/w/repo", "/w/Repo/src", true)]
    [InlineData("/w/repo-two", "/w/Repo", false)]
    public void A_case_insensitive_disk_treats_differently_cased_paths_as_the_same_folder(string a, string b, bool same)
    {
        Assert.Equal(same, FolderViews.Overlap(a, b, StringComparison.OrdinalIgnoreCase));
        if (same) Assert.False(FolderViews.Overlap(a, b, StringComparison.Ordinal));
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(5, limit.Token);
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
