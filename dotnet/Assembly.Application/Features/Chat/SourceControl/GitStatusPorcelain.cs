using System.Globalization;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Reads porcelain v2 status, keeping paths intact and counting files beyond the display limit.</summary>
public static class GitStatusPorcelain
{
    public static async Task<GitStatus> ParseAsync(IAsyncEnumerable<string> records)
    {
        string? branch = null, head = null;
        var ahead = 0;
        var behind = 0;
        var hasUpstream = false;
        var totalFiles = 0;
        var files = new List<ChangedFile>();
        await using var entries = records.GetAsyncEnumerator();
        while (await entries.MoveNextAsync())
        {
            var entry = entries.Current;
            if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
                head = entry[13..] is "(initial)" ? null : entry[13..];
            else if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = entry[14..] is "(detached)" ? null : entry[14..];
            // Git names an upstream that was deleted on the remote but prints no branch.ab for it, so only the counts
            // say there is one to compare with.
            else if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                hasUpstream = true;
                var counts = entry[12..].Split(' ');
                ahead = int.Parse(counts[0][1..], CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1][1..], CultureInfo.InvariantCulture);
            }
            else if (entry.Length > 0 && entry[0] is '1' or '2' or 'u' or '?')
            {
                var kind = entry[0];
                string? originalPath = null;
                if (kind == '2' && await entries.MoveNextAsync())
                    originalPath = entries.Current;
                totalFiles++;
                if (files.Count == 1000)
                    continue;
                // Limit the split to metadata fields so spaces inside a path survive.
                var fields = entry.Split(' ', kind switch { '1' => 9, '2' => 10, 'u' => 11, _ => 2 });
                // An unmerged entry's letters say how each side changed it (AA, DU, ...); it's a conflict either way.
                var (index, worktree) = kind switch { '?' => ('?', '?'), 'u' => ('U', 'U'), _ => (fields[1][0], fields[1][1]) };
                files.Add(new ChangedFile(fields[^1], originalPath, index, worktree));
            }
        }
        return new GitStatus(branch, head, hasUpstream ? ahead : 0, hasUpstream ? behind : 0,
            hasUpstream, files, totalFiles);
    }
}
