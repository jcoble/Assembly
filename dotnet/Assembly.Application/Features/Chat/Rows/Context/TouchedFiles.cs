namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>One grouped path from the rows. <c>MustExist</c>: only shell or prompt candidates named it.</summary>
internal sealed record TouchedRow(string Path, long Added, long Removed, long Count, long LastSeq, bool MustExist, string? Name);

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
        for (var offset = 0; offset < Budget && kept.Count < MostFiles; offset += Page)
        {
            var rows = page(Page, offset);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (Keep(row, folder, probe)) kept.Add(new FileTouched(row.Path, row.Added, row.Removed, row.Count, row.LastSeq, row.Name));
                if (kept.Count == MostFiles) break;
            }
            if (rows.Count < Page) break;
        }
        return kept;
    }

    private static bool Keep(TouchedRow row, string? folder, IPathProbe? probe)
    {
        if (folder is null || probe is null || row.Name is not null) return true;
        var full = Path.IsPathRooted(row.Path) ? row.Path : Path.Combine(folder, row.Path);
        if (probe.IsFolder(full)) return false;
        return !row.MustExist || probe.IsFile(full);
    }
}
