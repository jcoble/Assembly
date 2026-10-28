using System.Runtime.CompilerServices;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// Finds files and folders whose name holds the query, ignoring case, over what git lists in the checkout (tracked
/// files, including those in submodules, then untracked ones; not ignored ones; a folder counts once one of its files is
/// listed). A page starts after <c>cursor</c> matches and stops the walk once it knows whether another page follows.
/// </summary>
public static class NameSearch
{
    public const int MaxPageSize = 200;

    public static async Task<SearchPage> PageAsync(string root, string query, int cursor, int pageSize, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (cursor < 0)
            throw new CallFailed("expected a cursor of 0 or more");
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        root = SessionPaths.Resolve(root, "");
        var matches = new List<NameMatch>();
        if (query.Length == 0)
            return new SearchPage(matches, null, true);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var seen = 0;
        await foreach (var file in Files(root, cancellationToken))
        {
            // Each folder on the way, the first time it's seen, then the file.
            for (var slash = file.IndexOf('/'); slash > 0; slash = file.IndexOf('/', slash + 1))
            {
                var folder = file[..slash];
                if (folders.Add(folder) && Matched(folder, true))
                    return new SearchPage(matches, cursor + pageSize, false);
            }
            if (Matched(file, false))
                return new SearchPage(matches, cursor + pageSize, false);
        }
        return new SearchPage(matches, null, true);

        // git lists a submodule's files only with --recurse-submodules, which it allows for tracked files alone.
        static async IAsyncEnumerable<string> Files(string root, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var file in GitLines.ReadAsync(root, ["ls-files", "-z", "--cached", "--recurse-submodules"], null, cancellationToken))
                yield return file;
            await foreach (var file in GitLines.ReadAsync(root, ["ls-files", "-z", "--others", "--exclude-standard"], null, cancellationToken))
                yield return file;
        }

        // Counts a match and keeps it if it's on this page; true once one past the page shows another page follows.
        bool Matched(string path, bool isDirectory)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return false;
            // git's index still lists a file deleted but not staged; only what's on disk counts.
            var full = Path.Combine(root, path);
            if (isDirectory ? !Directory.Exists(full) : !File.Exists(full))
                return false;
            seen++;
            if (seen <= cursor)
                return false;
            if (matches.Count == pageSize)
                return true;
            matches.Add(new NameMatch(path, name, isDirectory, path));
            return false;
        }
    }
}
