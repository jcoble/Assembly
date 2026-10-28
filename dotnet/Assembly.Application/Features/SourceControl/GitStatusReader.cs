using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.SourceControl;

/// <summary>Reads a project's status in one git call without changing the index.</summary>
public sealed class GitStatusReader(IGit git)
{
    public async Task<GitStatus> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var output = await git.RunAsync(root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], cancellationToken);
        return GitStatusPorcelain.Parse(output);
    }
}
