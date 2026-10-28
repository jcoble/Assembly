using System.Globalization;

namespace Assembly.Application.Features.Worktrees;

/// <summary>
/// A project's worktrees, read with git in its root: the list, then each folder's status, unpushed count and head
/// commit. It only reads (scm#8): GitCli sets GIT_OPTIONAL_LOCKS=0, so status never rewrites the index.
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
            // Locked folders may be absent too; git deliberately does not mark them prunable.
            if (!row.IsBare && !row.Prunable && !row.Locked)
            {
                var status = await git.RunAsync(row.Path, ["status", "--porcelain=v1", "-z"], cancellationToken);
                row = row with { IsDirty = status.Length != 0 };
                if (row.Head is not null)
                {
                    var count = await git.RunAsync(row.Path, ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellationToken);
                    var log = await git.RunAsync(row.Path, ["log", "-1", "--format=%ct%x00%s"], cancellationToken);
                    var separator = log.IndexOf('\0');
                    row = row with
                    {
                        Unpushed = int.Parse(count.Trim(), CultureInfo.InvariantCulture),
                        HeadCommittedAt = long.Parse(log[..separator], CultureInfo.InvariantCulture) * 1000,
                        HeadSubject = log[(separator + 1)..].TrimEnd('\r', '\n')
                    };
                }
            }
            rows.Add(row);
        }
        return rows;
    }
}
