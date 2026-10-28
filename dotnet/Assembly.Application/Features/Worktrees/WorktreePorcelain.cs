namespace Assembly.Application.Features.Worktrees;

/// <summary>Reads git's NUL-separated worktree records without interpreting paths as quoted strings.</summary>
public static class WorktreePorcelain
{
    public static IReadOnlyList<Worktree> Parse(string output)
    {
        var rows = new List<Worktree>();
        foreach (var record in output.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var row = new Worktree("", null, null, rows.Count == 0, false, false, 0, false, false, null, null);
            foreach (var field in record.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = field.IndexOf(' ');
                var label = space < 0 ? field : field[..space];
                var value = space < 0 ? "" : field[(space + 1)..];
                row = label switch
                {
                    "worktree" => row with { Path = value },
                    "HEAD" => row with { Head = value.All(c => c == '0') ? null : value },
                    "branch" => row with { Branch = value["refs/heads/".Length..] },
                    "bare" => row with { IsBare = true, Head = null },
                    "detached" => row with { Branch = null },
                    "locked" => row with { Locked = true },
                    "prunable" => row with { Prunable = true },
                    _ => row
                };
            }
            rows.Add(row);
        }
        return rows;
    }
}
