using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitSyncTests
{
    // Pins: g9#5
    [Fact]
    public async Task Publish_sets_upstream_and_refuses_a_second_publish_or_changed_branch()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Commit("Root");
        var origin = Path.Combine(repo.Root, "remote.git"); await repo.Run("init", "--bare", origin); await repo.Run("remote", "add", "origin", origin);
        var sync = new GitSync(repo.Git); var published = await sync.PublishAsync(repo.Root, "main", "origin", default);
        Assert.True(published.Status.HasUpstream);
        await Assert.ThrowsAsync<CallFailed>(() => sync.PublishAsync(repo.Root, "main", "origin", default));
        await repo.Run("checkout", "-b", "another");
        await Assert.ThrowsAsync<CallFailed>(() => sync.PushAsync(repo.Root, "main", default));
        Assert.Equal("another", (await new GitStatusReader(repo.Git).ReadAsync(repo.Root, default)).Branch);
    }
    // Pins: g9#5
    [Fact]
    public async Task A_detached_head_is_refused_before_pull()
    {
        using var repo = new GitHistoryTestRepository(); await repo.InitializeAsync(); await repo.Commit("Root"); await repo.Run("checkout", "--detach");
        await Assert.ThrowsAsync<CallFailed>(() => new GitSync(repo.Git).PullAsync(repo.Root, "main", default));
    }
}
