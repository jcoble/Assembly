using System.Globalization;

namespace Assembly.Application.Features.SourceControl;

/// <summary>Reads porcelain v2 status, keeping paths intact and counting files beyond the display limit.</summary>
public static class GitStatusPorcelain
{
    public static GitStatus Parse(string output)
    {
        string? branch = null, head = null;
        var ahead = 0;
        var behind = 0;
        var hasUpstream = false;
        var totalFiles = 0;
        var files = new List<ChangedFile>();
        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
                head = entry[13..] is "(initial)" ? null : entry[13..];
            else if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = entry[14..] is "(detached)" ? null : entry[14..];
            else if (entry.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                hasUpstream = true;
            else if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = entry[12..].Split(' ');
                ahead = int.Parse(counts[0][1..], CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1][1..], CultureInfo.InvariantCulture);
            }
            else if (entry.Length > 0 && entry[0] is '1' or '2' or 'u' or '?')
            {
                var kind = entry[0];
                // Limit the split to metadata fields so spaces inside a path survive.
                var fields = entry.Split(' ', kind switch { '1' => 9, '2' => 10, 'u' => 11, _ => 2 });
                var originalPath = kind == '2' ? entries[++i] : null;
                totalFiles++;
                // An unmerged entry's letters say how each side changed it (AA, DU, ...); it's a conflict either way.
                var (index, worktree) = kind switch { '?' => ('?', '?'), 'u' => ('U', 'U'), _ => (fields[1][0], fields[1][1]) };
                if (files.Count < 1000)
                    files.Add(new ChangedFile(fields[^1], originalPath, index, worktree));
            }
        }
        return new GitStatus(branch, head, hasUpstream ? ahead : 0, hasUpstream ? behind : 0,
            hasUpstream, files, totalFiles);
    }
}
