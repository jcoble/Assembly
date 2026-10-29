using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>
/// Reads the branch's committed and working tree changes from its merge base with origin/HEAD (scm#18).
/// Untracked files stay out (scm#19); binary notices stay as git prints them. Reading changes nothing and stops
/// git when the text reaches the same cap as a file diff (scm#20).
/// </summary>
public sealed class GitBranchDiffReader(IGit git)
{
    public async Task<GitBranchDiff> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var baseName = await ReadRef(["rev-parse", "--verify", "--quiet", "--abbrev-ref", "origin/HEAD"]);
        if (baseName.Length == 0)
            throw new CallFailed("this folder's repository has no origin/HEAD, so there's no default branch to compare with");
        var mergeBase = await ReadRef(["merge-base", "HEAD", baseName]);
        if (mergeBase.Length == 0)
            throw new CallFailed($"this branch has nothing in common with {baseName}");
        var diff = await GitDiffReader.ReadText(git, root,
            ["--literal-pathspecs", "-c", "core.quotepath=false", "diff", "--no-ext-diff", "--no-color", "-M", mergeBase],
            GitDiffReader.Cap, cancellationToken);
        return new GitBranchDiff(baseName, diff.Text, diff.Truncated);

        async Task<string> ReadRef(IReadOnlyList<string> arguments)
        {
            var text = new StringBuilder();
            await foreach (var piece in git.ReadAsync(root, arguments, null, cancellationToken))
                text.Append(piece);
            return text.ToString().Trim();
        }
    }
}
