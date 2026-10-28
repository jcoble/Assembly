using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Reads a project's status in one git call without changing the index.</summary>
public sealed class GitStatusReader(IGit git)
{
    public Task<GitStatus> ReadAsync(string root, CancellationToken cancellationToken) =>
        GitStatusPorcelain.ParseAsync(git.ReadAsync(root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], null, cancellationToken));
}
