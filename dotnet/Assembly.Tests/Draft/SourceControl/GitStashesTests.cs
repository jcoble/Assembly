using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitStashesTests
{
    // Git strips trailing whitespace from the subject it stores, so "assembly s1 <time>: " comes back without its space.
    [Theory]
    [InlineData("", ":")]
    [InlineData("   ", ":")]
    [InlineData("wip  ", ": wip")]
    public async Task A_stash_whose_text_ends_in_space_is_found_after_git_strips_it(string text, string ending)
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Write("a", "base\n"); await repo.Commit("Root");
        var stashes = new GitStashes(repo.Git, TimeProvider.System);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "changed\n");

        var result = await stashes.StashAsync(repo.Root, "s1", text, false, default);

        var stash = Assert.Single(await stashes.ListAsync(repo.Root, default));
        Assert.Equal("Stashed changes as " + stash.Sha, result.Message);
        Assert.True(stash.MadeByAssembly);
        Assert.EndsWith(ending, stash.Message);
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "a")));
    }
    // Pins: g9#7
    [Fact]
    public async Task Applying_an_older_stash_drops_only_its_sha_and_keeps_the_newer_one()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Write("a", "base\n"); await repo.Commit("Root");
        var stashes = new GitStashes(repo.Git, TimeProvider.System);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "older\n"); await stashes.StashAsync(repo.Root, "s1", "Older", false, default);
        var older = Assert.Single(await stashes.ListAsync(repo.Root, default)); Assert.Equal("main", older.Branch); Assert.True(older.MadeByAssembly);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "newer\n"); await stashes.StashAsync(repo.Root, "s1", "Newer", false, default);
        var newest = (await stashes.ListAsync(repo.Root, default))[0];
        await stashes.ApplyAsync(repo.Root, older.Sha, default);
        Assert.Equal("older\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "a")));
        Assert.Equal(newest.Sha, Assert.Single(await stashes.ListAsync(repo.Root, default)).Sha);
    }
    // Pins: g9#7
    [Fact]
    public async Task A_conflicting_apply_keeps_the_exact_stash()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Write("a", "base\n"); await repo.Commit("Root");
        var stashes = new GitStashes(repo.Git, TimeProvider.System);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "stashed\n"); await stashes.StashAsync(repo.Root, "s1", "Save", false, default);
        var stash = Assert.Single(await stashes.ListAsync(repo.Root, default));
        await repo.Write("a", "committed elsewhere\n"); await repo.Commit("New base");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => stashes.ApplyAsync(repo.Root, stash.Sha, default));
        Assert.Contains("it's kept", failure.Message); Assert.Equal(stash.Sha, Assert.Single(await stashes.ListAsync(repo.Root, default)).Sha);
    }
    // Pins: g9#7
    [Fact]
    public async Task A_stash_inserted_between_lookup_and_drop_is_put_back_before_retry()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Write("a", "base\n"); await repo.Commit("Root");
        var other = Path.Combine(repo.Root, "other"); await repo.Run("worktree", "add", "-b", "other", other);
        var ordinary = new GitStashes(repo.Git, TimeProvider.System);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a"), "wanted\n"); await ordinary.StashAsync(repo.Root, "s1", "Wanted", false, default);
        var wanted = Assert.Single(await ordinary.ListAsync(repo.Root, default));
        var git = new BeforeDrop(repo.Git, async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(other, "a"), "racing\n");
            await foreach (var _ in repo.Git.ReadAsync(other, ["stash", "push", "-m", "Racing"], null, default)) { }
        });
        await new GitStashes(git, TimeProvider.System).DropAsync(repo.Root, wanted.Sha, default);
        var retained = Assert.Single(await ordinary.ListAsync(repo.Root, default));
        Assert.Contains("Racing", retained.Message); Assert.NotEqual(wanted.Sha, retained.Sha);
    }
    private sealed class BeforeDrop(IGit inner, Func<Task> before) : IGit
    {
        private int _ran;
        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input, CancellationToken ct) => inner.ReadAsync(folder, arguments, input, ct);
        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? input, TimeSpan? limit, CancellationToken ct)
        {
            if (arguments.Count > 1 && arguments[0] == "stash" && arguments[1] == "drop" && Interlocked.Exchange(ref _ran, 1) == 0) await before();
            return await inner.RunAsync(folder, arguments, input, limit, ct);
        }
    }
}
