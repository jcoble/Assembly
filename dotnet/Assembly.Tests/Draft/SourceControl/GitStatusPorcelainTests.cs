using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Reads the branch and changed paths from git's NUL-separated status format.</summary>
public sealed class GitStatusPorcelainTests
{
    // Pins: scm#9
    [Fact]
    public async Task Upstream_branch_deleted_on_the_remote_counts_as_no_upstream()
    {
        // Git names the upstream but prints no branch.ab when the remote branch is gone.
        var status = await Parse($"# branch.oid {new string('c', 40)}\0# branch.head main\0# branch.upstream origin/main\0");
        Assert.False(status.HasUpstream);
    }

    // Pins: scm#11
    [Fact]
    public async Task Caps_the_files_but_counts_every_entry()
    {
        var status = await Parse(string.Concat(Enumerable.Range(0, 1005).Select(i => $"? file {i}\0")));
        Assert.Equal(1000, status.Files.Count);
        Assert.Equal(1005, status.TotalFiles);
        Assert.Equal("file 999", status.Files[^1].Path);
    }

    private static Task<GitStatus> Parse(string output) => GitStatusPorcelain.ParseAsync(Records(output));

    private static async IAsyncEnumerable<string> Records(string output)
    {
        await Task.CompletedTask;
        foreach (var record in output.Split('\0'))
            yield return record;
    }
}
