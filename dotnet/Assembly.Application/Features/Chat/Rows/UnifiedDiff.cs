using System.Text;
using Assembly.Domain.Events;

using Assembly.Domain.Rows;
namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A tool's file changes as one unified diff, with how many lines it adds and removes, in all and per file.</summary>
internal sealed record UnifiedDiff(string Text, int Added, int Removed, IReadOnlyList<FileChange> Files)
{
    public static UnifiedDiff? Of(IReadOnlyList<FileDiff>? diffs)
    {
        if (diffs is null or { Count: 0 })
            return null;
        var (text, files) = (new StringBuilder(), new List<FileChange>());
        foreach (var diff in diffs)
        {
            text.Append(diff.Patch);
            var counts = diff.Paths.ToDictionary(path => path, _ => (Added: 0, Removed: 0));
            string? path = null;
            var inHunk = false;
            foreach (var raw in (diff.Patch ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    path = null;
                    inHunk = false;
                }
                else if (!inHunk && (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)))
                {
                    var name = line[4..];
                    if (name.StartsWith('"') && name.EndsWith('"')) name = Unquote(name);
                    if (name.StartsWith("a/", StringComparison.Ordinal) || name.StartsWith("b/", StringComparison.Ordinal)) name = name[2..];
                    path = diff.Paths.FirstOrDefault(candidate => candidate == name
                        || candidate.EndsWith('/' + name.TrimStart('/'), StringComparison.Ordinal)) ?? path;
                }
                else if (line.StartsWith("@@ ", StringComparison.Ordinal)) inHunk = true;
                else if (inHunk && path is not null)
                {
                    var count = counts[path];
                    if (line.StartsWith('+')) count.Added++;
                    else if (line.StartsWith('-')) count.Removed++;
                    counts[path] = count;
                }
            }
            files.AddRange(counts.Select(pair => new FileChange(pair.Key, pair.Value.Added, pair.Value.Removed)));
        }

        return new UnifiedDiff(text.ToString(), files.Sum(file => file.Added), files.Sum(file => file.Removed), files);
    }

    // Git quotes UTF-8 bytes with C escapes (including octal), rather than JSON escaping.
    private static string Unquote(string path)
    {
        var source = Encoding.UTF8.GetBytes(path[1..^1]);
        var bytes = new List<byte>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            if (b == '\\' && i + 1 < source.Length)
            {
                b = source[++i];
                if (b is >= (byte)'0' and <= (byte)'7')
                {
                    var value = b - '0';
                    for (var n = 1; n < 3 && i + 1 < source.Length && source[i + 1] is >= (byte)'0' and <= (byte)'7'; n++)
                        value = value * 8 + source[++i] - '0';
                    b = (byte)value;
                }
                else b = b switch { (byte)'t' => (byte)'\t', (byte)'n' => (byte)'\n', (byte)'r' => (byte)'\r', _ => b };
            }
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

}
