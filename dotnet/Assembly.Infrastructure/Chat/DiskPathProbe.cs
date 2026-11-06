using Assembly.Application.Features.Chat.Rows.Context;

namespace Assembly.Infrastructure.Chat;

/// <summary>Answers <see cref="IPathProbe"/> from this machine's disk.</summary>
public sealed class DiskPathProbe : IPathProbe
{
    public bool IsFile(string path) => File.Exists(path);
    public bool IsFolder(string path) => Directory.Exists(path);

    /// <summary>
    /// Follows each part's symlink from the root down, so a symlinked parent (a project link, or Mac's <c>/tmp</c> to
    /// <c>/private/tmp</c>) resolves too. A missing path, or a link that can't be followed, stays as given.
    /// </summary>
    public string Resolve(string path)
    {
        if (!Path.IsPathRooted(path) || !(File.Exists(path) || Directory.Exists(path))) return path;
        try
        {
            var full = Path.GetFullPath(path);
            var resolved = Path.GetPathRoot(full)!;
            foreach (var part in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Path.Combine(resolved, part);
                if (File.ResolveLinkTarget(resolved, returnFinalTarget: true) is { } target) resolved = Resolve(target.FullName);
            }
            return resolved;
        }
        catch (IOException) { return path; }
        catch (UnauthorizedAccessException) { return path; }
    }
}
