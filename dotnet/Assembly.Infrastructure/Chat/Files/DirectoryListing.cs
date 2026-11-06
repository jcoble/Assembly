using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One folder of a session's checkout: folders first, then files, by name. Entries git ignores, and <c>.git</c>, are
/// marked excluded and left out unless asked for; in a folder that isn't a git repo nothing is ignored. Never more than <see cref="Limit"/>, so an answer stays well inside a frame.
/// </summary>
public static class DirectoryListing
{
    public const int Limit = 500;
    private const int PageBytes = 1024 * 1024;

    public static async Task<FileListing> ListAsync(string root, string dir, bool includeExcluded, CancellationToken cancellationToken,
        bool descending = false, PageCursor? cursor = null)
    {
        var folder = SessionPaths.ResolveFile(root, dir, allowRoot: true);
        root = SessionPaths.Resolve(root, "");
        if (!Directory.Exists(folder)) throw new CallFailed("that folder isn't there");
        using var lease = DirectoryGeneration.Shared.Observe(root);
        var generation = lease.Generation;
        if (cursor is not null && cursor.Generation != generation) throw new CallFailed("cursor_changed: refresh this folder");
        var prefix = folder == root ? "" : Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/') + "/";
        var comparer = Comparer<FileEntry>.Create((a, b) => Compare(a, b, descending));
        var candidates = new PriorityQueue<FileEntry, FileEntry>(Comparer<FileEntry>.Create((a, b) => comparer.Compare(b, a)));
        var batch = new List<(string Name, bool Directory, bool Link)>(128);
        var after = cursor is null ? null : Decode(cursor.After);
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new FileEntry(prefix + info.Name, info.Name, info is DirectoryInfo, false);
            if (after is not null && comparer.Compare(entry, after) <= 0) continue;
            batch.Add((info.Name, info is DirectoryInfo, info.LinkTarget is not null));
            if (batch.Count == 128) await AddBatch();
        }
        await AddBatch();
        // A first page has nothing before it to disagree with; a later page must come from the listing it continues.
        if (cursor is not null && lease.Generation != generation) throw new CallFailed("cursor_changed: refresh this folder");
        var sorted = candidates.UnorderedItems.Select(item => item.Element).Order(comparer).ToList();
        var entries = new List<FileEntry>(Limit);
        var bytes = 4096;
        foreach (var entry in sorted.Take(Limit))
        {
            var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry, FilesJson.Default.FileEntry).Length + 1;
            if (size > PageBytes - 4096) throw new CallFailed("a directory entry is too large to display");
            if (bytes + size > PageBytes) break;
            entries.Add(entry); bytes += size;
        }
        var more = sorted.Count > entries.Count;
        return new FileListing(entries, more, more ? new PageCursor(generation, Encode(entries[^1])) : null);

        async Task AddBatch()
        {
            if (batch.Count == 0) return;
            var ignored = await IgnoredAsync(folder, batch.Select(e => e.Name + (e.Directory && !e.Link ? "/" : "")).ToArray(), cancellationToken);
            foreach (var item in batch)
            {
                var excluded = item.Name == ".git" || ignored.Contains(item.Name + (item.Directory && !item.Link ? "/" : ""));
                if (!includeExcluded && excluded) continue;
                var entry = new FileEntry(prefix + item.Name, item.Name, item.Directory, excluded);
                candidates.Enqueue(entry, entry);
                if (candidates.Count > Limit + 1) candidates.Dequeue();
            }
            batch.Clear();
        }
    }

    private static int Compare(FileEntry a, FileEntry b, bool descending)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        var compared = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        if (compared == 0) compared = StringComparer.Ordinal.Compare(a.Name, b.Name);
        return descending ? -compared : compared;
    }
    private static string Encode(FileEntry entry) => (entry.IsDirectory ? "d:" : "f:") + entry.Name;
    private static FileEntry Decode(string value)
    {
        if (value.Length < 3 || value[1] != ':' || value[0] is not ('d' or 'f') || value[2..].IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new CallFailed("invalid directory cursor");
        return new FileEntry("", value[2..], value[0] == 'd', false);
    }

    private static async Task<HashSet<string>> IgnoredAsync(string folder, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return ignored;
        try
        {
            await foreach (var path in new GitCli().ReadAsync(folder, ["check-ignore", "-z", "--stdin"], paths, cancellationToken))
                ignored.Add(path);
        }
        catch (CallFailed failed) when (failed.Message.Contains("not a git repository", StringComparison.Ordinal))
        {
            // A folder git can't answer for (it isn't a repo) has nothing ignored; the listing still shows.
            ignored.Clear();
        }
        return ignored;
    }
}
