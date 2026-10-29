using System.Text;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>
/// One file's staged and working tree diffs (scm#14). The two together stop at <see cref="Cap"/> characters, which
/// stops git, so even a diff of escaped characters stays well under the socket's frame limit. A binary side has no
/// text and sets <c>Binary</c>; the other side keeps its own (scm#16). An untracked file's working tree diff shows
/// every line as added. Paths are read as written, never as patterns ("a[1].txt", ":(top)x").
/// </summary>
public sealed class GitDiffReader(IGit git)
{
    public const int Cap = 512 * 1024;

    public async Task<GitFileDiff> ReadAsync(string root, string path, string? originalPath, CancellationToken cancellationToken)
    {
        List<string> stagedArgs = ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-color", "--cached", "-M", "--"];
        if (originalPath is not null) stagedArgs.Add(originalPath);
        stagedArgs.Add(path);
        var staged = await ReadText(git, root, stagedArgs, Cap, cancellationToken);
        var untracked = false;
        await foreach (var record in git.ReadAsync(root, ["--literal-pathspecs", "ls-files", "--others", "--exclude-standard", "--", path], null, cancellationToken))
        {
            if (record.Length == 0) continue;
            untracked = true;
            break;
        }
        var unstaged = await ReadText(git, root, untracked
            ? ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-color", "--no-index", "--", "/dev/null", path]
            : ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-color", "--", path], Cap - staged.Text.Length, cancellationToken);
        var (stagedBinary, unstagedBinary) = (IsBinary(staged.Text), IsBinary(unstaged.Text));
        return new GitFileDiff(path, stagedBinary ? "" : staged.Text, unstagedBinary ? "" : unstaged.Text,
            stagedBinary || unstagedBinary, staged.Truncated || unstaged.Truncated);
    }

    internal static async Task<(string Text, bool Truncated)> ReadText(IGit git, string root,
        IReadOnlyList<string> arguments, int cap, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var piece in git.ReadAsync(root, arguments, null, cancellationToken))
        {
            text.Append(piece.AsSpan(0, Math.Min(piece.Length, cap - text.Length)));
            if (text.Length < cap)
                continue;
            // A cut between the two halves of a character would leave half of it, which doesn't read as JSON.
            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
                text.Length--;
            return (text.ToString(), true);
        }
        return (text.ToString(), false);
    }

    private static bool IsBinary(string text) =>
        text.StartsWith("Binary files ", StringComparison.Ordinal) || text.Contains("\nBinary files ", StringComparison.Ordinal);
}
