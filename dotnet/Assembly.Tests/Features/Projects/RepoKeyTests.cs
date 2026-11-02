using Assembly.Application.Features.Projects;

namespace Assembly.Tests.Features.Projects;

public sealed class RepoKeyTests
{
    [Theory]
    [InlineData("https://github.com/Owner/Repo.git", "github.com/Owner/Repo")]
    [InlineData("ssh://git@GitHub.com:22/owner/repo/", "github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "github.com/owner/repo")]
    [InlineData("https://user@host.example/a/b", "host.example/a/b")]
    public void Remote_urls_normalize(string url, string key) => Assert.Equal(key, RepoKey.Normalize(url));
    [Theory]
    [InlineData("git://host/a/b")]
    [InlineData("/srv/repo.git")]
    [InlineData("file:///srv/repo")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsupported_remote_urls_have_no_key(string? url) => Assert.Null(RepoKey.Normalize(url));
}
