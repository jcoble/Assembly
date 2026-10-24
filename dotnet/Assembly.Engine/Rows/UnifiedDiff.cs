using System.Text;
using DiffPlex.Renderer;
using Assembly.Engine.Events;

namespace Assembly.Engine.Rows;

/// <summary>A tool's file changes as one unified diff, with how many lines it adds and removes.</summary>
internal sealed record UnifiedDiff(string Text, int Added, int Removed)
{
    private static readonly UnidiffRenderer Renderer = new(contextLines: 3);

    public static UnifiedDiff? Of(IReadOnlyList<FileDiff>? diffs)
    {
        if (diffs is null or { Count: 0 })
            return null;
        var (text, added, removed) = (new StringBuilder(), 0, 0);
        foreach (var diff in diffs)
        {
            var one = Renderer.Generate(TrimEnd(diff.OldText ?? ""), TrimEnd(diff.NewText), diff.Path, diff.Path, ignoreWhitespace: false);
            text.Append(one);
            // Counted one file at a time: its --- and +++ lines come before its first hunk, and name the file.
            foreach (var line in one.Split('\n').SkipWhile(line => !line.StartsWith("@@", StringComparison.Ordinal)))
            {
                if (line.StartsWith('+'))
                    added++;
                else if (line.StartsWith('-'))
                    removed++;
            }
        }

        return text.Length == 0 ? null : new UnifiedDiff(text.ToString(), added, removed);
    }

    // A final newline would otherwise show up as an extra empty line in the diff.
    private static string TrimEnd(string text) => text.EndsWith('\n') ? text[..^1] : text;
}
