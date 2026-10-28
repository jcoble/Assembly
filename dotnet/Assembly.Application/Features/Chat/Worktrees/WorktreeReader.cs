using System.Globalization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.Worktrees;

/// <summary>
/// A project's worktrees, read with git in its root: the list, then each folder's status and each head's unpushed count
/// and commit. It only reads (scm#8): GitCli sets GIT_OPTIONAL_LOCKS=0, so status never rewrites the index.
/// </summary>
public sealed class WorktreeReader(IGit git)
{
    public async Task<IReadOnlyList<Worktree>> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var output = await git.RunAsync(root, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        var rows = new List<Worktree>();
        foreach (var entry in WorktreePorcelain.Parse(output))
        {
            var row = entry;
            if (!row.IsBare && !row.Prunable)
            {
                try
                {
                    var status = await git.RunAsync(row.Path, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"], cancellationToken);
                    row = row with { IsDirty = status.Length != 0 };
                }
                // Git doesn't mark a locked worktree prunable when its folder is gone (an unplugged drive, say), so a
                // locked one whose folder git can't enter keeps its marks without its status.
                catch (CallFailed) when (row.Locked)
                {
                }
            }
            rows.Add(row.Head is null ? row : await ReadHeadAsync(root, row, cancellationToken));
        }
        return rows;
    }

    /// <summary>
    /// The listed head's unpushed count and commit, read in the root, since the commits are shared: a worktree whose
    /// folder is gone still says what would be lost, and a commit made since the list doesn't mix into the row.
    /// </summary>
    private async Task<Worktree> ReadHeadAsync(string root, Worktree row, CancellationToken cancellationToken)
    {
        var count = await git.RunAsync(root, ["rev-list", "--count", row.Head!, "--not", "--remotes"], cancellationToken);
        var log = await git.RunAsync(root, ["log", "-1", "--no-show-signature", "--format=%ct%x00%s", row.Head!], cancellationToken);
        var separator = log.IndexOf('\0');
        return row with
        {
            Unpushed = int.Parse(count.Trim(), CultureInfo.InvariantCulture),
            HeadCommittedAt = long.Parse(log[..separator], CultureInfo.InvariantCulture) * 1000,
            HeadSubject = log[(separator + 1)..].TrimEnd('\r', '\n')
        };
    }
}
