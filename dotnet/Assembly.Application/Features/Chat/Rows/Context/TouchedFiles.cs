namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>
/// One grouped path from the rows. <c>Calls</c>: the calls and prompts that named it, so two spellings of one file that
/// a single call carries count that call once. <c>MustExist</c>: only shell or prompt candidates named it.
/// </summary>
internal sealed record TouchedRow(string Path, long Added, long Removed, IReadOnlyCollection<string> Calls, long LastSeq, bool MustExist, string? Name);

/// <summary>
/// Pages the grouped paths and checks the disk until 50 are kept, since a shell-heavy session can have many candidates
/// that aren't files and a short list would hide real ones.
/// </summary>
internal static class TouchedFiles
{
    public const int MostFiles = 50, Page = 200, Budget = 1000;

    /// <summary>
    /// Without a <paramref name="folder"/> and a <paramref name="probe"/> there's no disk here, so nothing is checked (a
    /// remote session's copied rows).
    /// </summary>
    public static IReadOnlyList<FileTouched> Read(Func<int, int, IReadOnlyList<TouchedRow>> page, string? folder, IPathProbe? probe, CancellationToken ct)
    {
        var kept = new List<FileTouched>();
        var calls = new List<HashSet<string>>();
        var real = folder is null || probe is null ? null : probe.Resolve(folder);
        for (var offset = 0; offset < Budget && kept.Count < MostFiles; offset += Page)
        {
            var rows = page(Page, offset);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (Keep(row, folder, probe)) Add(kept, calls, new FileTouched(Inside(row, folder, real, probe), row.Added, row.Removed, row.Calls.Count, row.LastSeq, row.Name), row.Calls);
                if (kept.Count == MostFiles) break;
            }
            if (rows.Count < Page) break;
        }
        return kept;
    }

    /// <summary>
    /// Each path as one key: a relative one joins the folder as a tool row's does (<see cref="ToolSummary.InFolder"/>), so
    /// <c>./x</c> lists as <c>x</c> and a <c>..</c> that leaves the folder lists absolute, as that file's whole path does.
    /// A whole path that resolves inside the resolved folder, as one reached through the real path of a symlinked folder
    /// does, lists relative like any other inside file. The query strips only the folder as written.
    /// </summary>
    private static string Inside(TouchedRow row, string? folder, string? real, IPathProbe? probe)
    {
        if (row.Name is not null) return row.Path;
        var path = ToolSummary.InFolder(row.Path, null, folder) ?? row.Path;
        if (real is null || probe is null || !Path.IsPathRooted(path)) return path;
        var resolved = probe.Resolve(path);
        return resolved.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? resolved[(real.Length + 1)..] : path;
    }

    /// <summary>
    /// The same file under two spellings is one row: the newer keeps its place, the lines add and the count is the
    /// distinct calls across both, so one call that carries both spellings counts once.
    /// </summary>
    private static void Add(List<FileTouched> kept, List<HashSet<string>> calls, FileTouched file, IReadOnlyCollection<string> named)
    {
        var at = kept.FindIndex(held => held.Path == file.Path && held.Name == file.Name);
        if (at < 0) { kept.Add(file); calls.Add([.. named]); return; }
        calls[at].UnionWith(named);
        var held = kept[at];
        kept[at] = held with { Added = held.Added + file.Added, Removed = held.Removed + file.Removed, Count = calls[at].Count };
    }

    private static bool Keep(TouchedRow row, string? folder, IPathProbe? probe)
    {
        if (folder is null || probe is null || row.Name is not null) return true;
        var full = Path.IsPathRooted(row.Path) ? row.Path : Path.Combine(folder, row.Path);
        if (probe.IsFolder(full)) return false;
        return !row.MustExist || probe.IsFile(full);
    }
}
