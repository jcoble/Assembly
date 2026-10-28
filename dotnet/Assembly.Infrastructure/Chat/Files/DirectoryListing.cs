using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One folder of a session's checkout: folders first, then files, by name. Entries git ignores, and <c>.git</c>, are
/// marked excluded and left out unless asked for; in a folder that isn't a git repo nothing is ignored. Never more than <see cref="Limit"/>, so an answer stays well inside a frame.
/// </summary>
public static class DirectoryListing
{
    public const int Limit = 2000;

    public static async Task<FileListing> ListAsync(string root, string dir, bool includeExcluded, CancellationToken cancellationToken)
    {
        var folder = SessionPaths.Resolve(root, dir);
        root = SessionPaths.Resolve(root, "");
        if (!Directory.Exists(folder))
            throw new CallFailed("that folder isn't there");
        var prefix = folder == root ? "" : Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/') + "/";
        var found = new List<(string Name, bool IsDirectory, bool IsLink)>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A link to a folder lists as one; listing it is refused, since nothing is read through a link.
            found.Add((info.Name, info is DirectoryInfo, info.LinkTarget is not null));
        }
        found.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? a.IsDirectory ? -1 : 1
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) is var order and not 0 ? order : string.CompareOrdinal(a.Name, b.Name));
        // Shown with the excluded ones, only the first Limit need asking about; without them, all may.
        var asked = includeExcluded ? found.Take(Limit).ToList() : found;
        // Asked from the folder itself, by name, so a folder inside a submodule is asked of the submodule's own git.
        // git refuses "link/" as a path beyond a link, so a link is asked by its bare name.
        static string Asked((string Name, bool IsDirectory, bool IsLink) entry) => entry.Name + (entry.IsDirectory && !entry.IsLink ? "/" : "");
        var ignored = await IgnoredAsync(folder, [.. asked.Select(Asked)], cancellationToken);
        var entries = asked
            .Select(entry => new FileEntry(prefix + entry.Name, entry.Name, entry.IsDirectory, entry.Name == ".git" || ignored.Contains(Asked(entry))))
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
