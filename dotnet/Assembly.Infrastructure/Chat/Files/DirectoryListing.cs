using Assembly.Infrastructure.Git;
namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One folder of a session's checkout: folders first, then files, by name. Entries git ignores, and <c>.git</c>, are
/// marked excluded and left out unless asked for. Never more than <see cref="Limit"/>, so an answer stays well inside a frame.
/// </summary>
public static class DirectoryListing
{
    public const int Limit = 2000;

    public static async Task<FileListing> ListAsync(string root, string dir, bool includeExcluded, CancellationToken cancellationToken)
    {
        var folder = SessionPaths.Resolve(root, dir);
        root = SessionPaths.Resolve(root, "");
        if (!Directory.Exists(folder))
            throw new Application.Common.Calls.CallFailed("that folder isn't there");
        var prefix = folder == root ? "" : Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/') + "/";
        var found = new List<(string Name, bool IsDirectory)>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A link is never a folder here, so nothing is listed through one.
            found.Add((info.Name, info is DirectoryInfo && info.LinkTarget is null));
        }
        found.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? a.IsDirectory ? -1 : 1
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) is var order and not 0 ? order : string.CompareOrdinal(a.Name, b.Name));
        // Shown with the excluded ones, only the first Limit need asking about; without them, all may.
        var asked = includeExcluded ? found.Take(Limit).ToList() : found;
        // Asked from the folder itself, by name, so a folder inside a submodule is asked of the submodule's own git.
        var ignored = await IgnoredAsync(folder, [.. asked.Select(entry => entry.Name + (entry.IsDirectory ? "/" : ""))], cancellationToken);
        var entries = asked
            .Select(entry => new FileEntry(prefix + entry.Name, entry.Name, entry.IsDirectory,
                entry.Name == ".git" || ignored.Contains(entry.Name + (entry.IsDirectory ? "/" : ""))))
            .Where(entry => includeExcluded || !entry.Excluded)
            .ToList();
        var shown = includeExcluded ? found.Count : entries.Count;
        return new FileListing(entries.Take(Limit).ToList(), shown > Limit);
    }

    private static async Task<HashSet<string>> IgnoredAsync(string folder, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return ignored;
        await foreach (var path in new GitCli().ReadAsync(folder, ["check-ignore", "-z", "--stdin"], paths, cancellationToken))
            ignored.Add(path);
        return ignored;
    }
}
