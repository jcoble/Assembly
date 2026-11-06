using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitBranchesTests
{
    // Pins: g9#3
    [Fact]
    public async Task Pure_creation_keeps_the_current_checkout_and_stale_head_creates_nothing()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Commit("Root");
        var branches = new GitBranches(repo.Git); var head = await repo.Head();
        await branches.CreateAsync(repo.Root, "feature", head, default);
        var list = await branches.ListAsync(repo.Root, default);
        Assert.Equal("main", Assert.Single(list.Branches, branch => branch.Current).Name);
        Assert.Contains(list.Branches, branch => branch.Name == "feature"); Assert.Equal(head, await repo.Head());
        await repo.Commit("Newer");
        await Assert.ThrowsAsync<CallFailed>(() => branches.CreateAsync(repo.Root, "stale", head, default));
        Assert.DoesNotContain((await branches.ListAsync(repo.Root, default)).Branches, branch => branch.Name == "stale");
    }
    [Fact]
    public async Task A_viewed_folder_switches_to_a_branch_or_creates_and_switches_at_the_head_it_saw()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Commit("Root");
        var branches = new GitBranches(repo.Git); var head = await repo.Head();
        await branches.SwitchAsync(repo.Root, "made", create: true, head, default);
        Assert.Equal("made", Assert.Single((await branches.ListAsync(repo.Root, default)).Branches, branch => branch.Current).Name);
        await branches.SwitchAsync(repo.Root, "main", create: false, null, default);
        Assert.Equal("main", Assert.Single((await branches.ListAsync(repo.Root, default)).Branches, branch => branch.Current).Name);
        await repo.Commit("Newer");
        await Assert.ThrowsAsync<CallFailed>(() => branches.SwitchAsync(repo.Root, "stale", create: true, head, default));
        Assert.DoesNotContain((await branches.ListAsync(repo.Root, default)).Branches, branch => branch.Name == "stale");
    }
    // Pins: g9#3
    [Theory]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("bad branch")]
    [InlineData("a..b")]
    [InlineData("/bad")]
    [InlineData("bad/")]
    [InlineData("bad.lock")]
    [InlineData("bad:ref")]
    public void Unsafe_branch_names_have_a_plain_refusal(string name) => Assert.NotNull(GitBranches.Refusal(name));

    // Pins: g9#3
    [Fact]
    public async Task Listing_keeps_a_branch_held_by_another_owned_worktree()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Commit("Root");
        var worktree = Path.Combine(repo.Root, "linked"); await repo.Run("worktree", "add", "-b", "held", worktree);
        var held = Assert.Single((await new GitBranches(repo.Git).ListAsync(repo.Root, default)).Branches, branch => branch.Name == "held");
        Assert.Equal(worktree, held.WorktreeFolder); Assert.False(held.Current);
    }
}
