using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("diff")]
    [InlineData("branchDiff")]
    [InlineData("history")]
    [InlineData("commitFiles")]
    [InlineData("commitDiff")]
    public async Task Six_read_methods_inspect_a_linked_checkout_without_mutating_the_session(string method)
    {
        await using var f = await Fixture.Start();
        using var client = await f.Open();
        var choices = await f.Call(client, "checkouts", f.Args());
        Assert.Equal(f.Root, choices.GetProperty("result").GetProperty("sessionRoot").GetString());
        var choice = Assert.Single(choices.GetProperty("result").GetProperty("worktrees").EnumerateArray());
        Assert.Equal(f.Linked, choice.GetProperty("path").GetString());
        Assert.Equal(2, f.Git.Count);
        f.Git.Reset();
        var selected = await f.Call(client, method, f.Args(selected: true));
        Assert.Equal("result", selected.GetProperty("t").GetString());
        var selectedCommands = f.Git.Commands.ToArray();
        Assert.Equal(8, selectedCommands.Count(IsValidation));
        f.Git.Reset();
        var ordinary = await f.Call(client, method, f.Args());
        Assert.Equal("result", ordinary.GetProperty("t").GetString());
        Assert.DoesNotContain(f.Git.Commands, IsValidation);
        Assert.Equal(f.Root, f.Owner.Sessions.Root(f.Owner.Sessions.Get(f.Session)!));
        Assert.Equal("main", (await Fixture.Run(f.Root, "branch", "--show-current")).Trim());
        AssertCode(await f.Call(client, "stage", new { session = f.Session, paths = new[] { "tracked.txt" }, expectedSource = f.Source, expectedCheckoutRoot = f.Linked }), "source_changed");
        Assert.Equal("", await Fixture.Run(f.Root, "diff", "--cached", "--name-only"));
        if (method == "status")
        {
            Assert.Equal("linked", selected.GetProperty("result").GetProperty("branch").GetString());
            Assert.Equal("main", ordinary.GetProperty("result").GetProperty("branch").GetString());
        }
    }

    [Theory]
    [InlineData("branches")]
    [InlineData("stashes")]
    [InlineData("checkouts")]
    public async Task Shared_noninspection_reads_reject_checkout_selectors(string method)
    {
        await using var f = await Fixture.Start(); using var client = await f.Open();
        AssertCode(await f.Call(client, method, f.Args(selected: true)), "bad_args");
        Assert.Equal(0, f.Git.Count);
        Assert.Equal("result", (await f.Call(client, method, f.Args())).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Invalid_selector_pairs_are_rejected_before_git()
    {
        await using var f = await Fixture.Start(); using var client = await f.Open();
        foreach (var args in new object[]
        {
            new { session=f.Session, checkoutPath=f.Linked, expectedSource=f.Source },
            new { session=f.Session, expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath=f.Linked, expectedCheckoutRoot=f.Root },
            new { session=(string?)null, checkoutPath=f.Linked, expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath="", expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath="relative", expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath=f.Linked+"/../linked", expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath="/"+new string('a',4096), expectedCheckoutRoot=f.Root, expectedSource=f.Source },
            new { session=f.Session, checkoutPath="/bad\0path", expectedCheckoutRoot=f.Root, expectedSource=f.Source }
        }) AssertCode(await f.Call(client, "status", args), "bad_args");
        AssertCode(await f.Call(client, "checkouts", new { session = f.Session }), "bad_args");
        Assert.Equal(0, f.Git.Count);
    }

    [Fact]
    public async Task Missing_pruned_foreign_and_symlink_checkouts_are_refused()
    {
        await using var f = await Fixture.Start(); using var client = await f.Open();
        var foreign = Path.Combine(f.Home, "foreign"); await Fixture.Repository(foreign);
        var bare = Path.Combine(f.Home, "bare"); Directory.CreateDirectory(bare); await Fixture.Run(bare, "init", "--bare");
        var alias = Path.Combine(f.Home, "alias"); Directory.CreateSymbolicLink(alias, f.Linked);
        foreach (var path in new[] { foreign, bare, alias, Path.Combine(f.Home, "missing"), f.Root })
            AssertCode(await f.Call(client, "status", f.Args(checkout: path)), "checkout_unavailable");
        Directory.Delete(f.Linked, true);
        AssertCode(await f.Call(client, "status", f.Args(selected: true)), "checkout_unavailable");
        await Fixture.Run(f.Root, "worktree", "prune");
        AssertCode(await f.Call(client, "status", f.Args(selected: true)), "checkout_unavailable");
        Directory.Delete(f.Root, true);
        AssertCode(await f.Call(client, "checkouts", f.Args()), "checkout_unavailable");
    }

    [Fact]
    public async Task Recorded_root_change_refuses_the_captured_read()
    {
        await using var f = await Fixture.Start(); using var client = await f.Open();
        f.Git.BeforeRead = (_, args) =>
        {
            if (args[0] == "status") f.SetRoot(f.Linked);
            return Task.CompletedTask;
        };
        AssertCode(await f.Call(client, "status", f.Args(selected: true)), "checkout_root_changed");
    }

    [Fact]
    public async Task Checkouts_excludes_the_actual_own_row_for_symlink_and_subfolder_recorded_roots()
    {
        await using var f = await Fixture.Start(); using var client = await f.Open();
        var alias = Path.Combine(f.Home, "own-alias"); Directory.CreateSymbolicLink(alias, f.Root);
        var sub = Path.Combine(f.Root, "subfolder"); Directory.CreateDirectory(sub);
        foreach (var root in new[] { alias, sub })
        {
            f.SetRoot(root); f.Git.Reset();
            var result = (await f.Call(client, "checkouts", f.Args())).GetProperty("result");
            Assert.Equal(Path.GetFullPath(root), result.GetProperty("sessionRoot").GetString());
            Assert.Equal(f.Linked, Assert.Single(result.GetProperty("worktrees").EnumerateArray()).GetProperty("path").GetString());
            Assert.Equal(2, f.Git.Count);
        }
    }

    private static bool IsValidation(string[] args) => args[0] == "worktree" || args[0] == "rev-parse" &&
        (args.Contains("--show-toplevel") || args.Contains("--git-common-dir"));
    private static void AssertCode(JsonElement frame, string code)
    {
        Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Equal(code, frame.GetProperty("code").GetString());
        Assert.False(frame.TryGetProperty("result", out _));
    }
}
