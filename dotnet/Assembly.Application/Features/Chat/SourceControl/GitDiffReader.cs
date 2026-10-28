using System.Text;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Reads separate staged and working tree diffs (scm#14), capped with binary notices (scm#16).</summary>
public sealed class GitDiffReader(IGit git)
{
    public async Task<GitFileDiff> ReadAsync(string root, string path, string? originalPath, CancellationToken cancellationToken)
    {
        List<string> stagedArgs = ["diff", "--no-ext-diff", "--no-color", "--cached", "-M", "--"];
        if (originalPath is not null) stagedArgs.Add(originalPath);
        stagedArgs.Add(path);
        var staged = await ReadText(stagedArgs);
        var tracked = false;
        await foreach (var record in git.ReadAsync(root, ["ls-files", "--", path], null, cancellationToken))
        {
            if (record.Length == 0) continue;
            tracked = true;
            break;
        }
        var unstaged = await ReadText(tracked
            ? ["diff", "--no-ext-diff", "--no-color", "--", path]
            : ["diff", "--no-ext-diff", "--no-color", "--no-index", "--", "/dev/null", path]);
        var binary = IsBinary(staged.Text) || IsBinary(unstaged.Text);
        return new GitFileDiff(path, binary ? "" : staged.Text, binary ? "" : unstaged.Text,
            binary, staged.Truncated || unstaged.Truncated);

        async Task<(string Text, bool Truncated)> ReadText(IReadOnlyList<string> arguments)
        {
            // The limit counts UTF-16 characters, independently for each diff.
            const int cap = 1024 * 1024;
            var text = new StringBuilder();
            await foreach (var piece in git.ReadAsync(root, arguments, null, cancellationToken))
            {
                text.Append(piece.AsSpan(0, Math.Min(piece.Length, cap - text.Length)));
                if (text.Length == cap) return (text.ToString(), true);
            }
            return (text.ToString(), false);
        }
    }

    private static bool IsBinary(string text) =>
        text.StartsWith("Binary files ", StringComparison.Ordinal) || text.Contains("\nBinary files ", StringComparison.Ordinal);
}
