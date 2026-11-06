using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitHubRepositoryTests
{
    // Pins: g9#12, g9#19
    [Theory]
    [InlineData("git@github.com:owner/repo.git", "owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("https://github.com/owner/repo.git/", "owner/repo")]
    [InlineData("https://github.com.evil/owner/repo", null)]
    [InlineData("https://gitlab.com/owner/repo", null)]
    [InlineData("https://github.com/owner/repo/extra", null)]
    [InlineData("https://github.com/owner/repo/../other", null)]
    [InlineData("https://github.com/owner/%2e%2e", null)]
    public void Remote_urls_name_only_the_exact_github_repository(string url, string? expected) => Assert.Equal(expected, GitHubRepository.FromRemoteUrl(url));
    // Pins: g9#19
    [Theory]
    [InlineData("owner/..")]
    [InlineData("owner/.")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/repo repo:other/repo")]
    [InlineData("-R evil")]
    public void Invalid_repository_search_scopes_are_refused(string repository) => Assert.Throws<CallFailed>(() => GitHubRepository.Parse(repository));
}
