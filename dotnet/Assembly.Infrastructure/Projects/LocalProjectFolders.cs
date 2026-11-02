using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Projects;

namespace Assembly.Infrastructure.Projects;

/// <summary>Read-only filesystem and Git facts on this owning host.</summary>
internal sealed partial class LocalProjectFolders(IGit git) : IProjectFolders
{
    public Task<FolderListing> ListAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Canonical(path);
        try
        {
            var dirs = new List<string>();
            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                dirs.Add(entry);
                if (dirs.Count == 501) break;
            }
            var truncated = dirs.Count > 500;
            if (truncated) dirs.RemoveAt(500);
            dirs.Sort(StringComparer.Ordinal);
            return Task.FromResult(new FolderListing(root, dirs, truncated));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CallFailed(e.Message); }
    }

    public async Task<FolderInspection> InspectAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Canonical(path);
        if (await TextAsync(root, ["rev-parse", "--is-bare-repository"], ct, optional: true) == "true")
            throw new CallFailed("This is a bare repository, so it has no working files. Choose one of its checkouts instead.");
        if (await TextAsync(root, ["rev-parse", "--is-inside-git-dir"], ct, optional: true) == "true")
            throw new CallFailed("This is git's internal folder. Choose the repository folder instead.");
        var top = await TextAsync(root, ["rev-parse", "--show-toplevel"], ct, optional: true);
        var title = Path.GetFileName(root);
        if (title.Length == 0) title = root;
        if (top is null) return new(root, title, null, false, null, false);
        if (Canonical(top) != root)
            throw new CallFailed($"This folder is inside the repository at {top}. Choose that folder instead.");
        var common = await TextAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        var url = await TextAsync(root, ["config", "--get", "remote.upstream.url"], ct, optional: true)
            ?? await TextAsync(root, ["config", "--get", "remote.origin.url"], ct, optional: true);
        var key = RepoKey.Normalize(url);
        var branch = await TextAsync(root, ["symbolic-ref", "-q", "HEAD"], ct, optional: true);
        var head = await TextAsync(root, ["rev-parse", "--verify", "-q", "HEAD"], ct, optional: true);
        return new(root, key is null ? title : key[(key.LastIndexOf('/') + 1)..], key, true,
            common is null ? null : Canonical(common), branch is not null && head is null);
    }

    public Task<IReadOnlyList<Worktree>> WorktreesAsync(string root, CancellationToken ct) => new WorktreeReader(git).ReadAsync(root, ct);

    private async Task<string?> TextAsync(string root, IReadOnlyList<string> args, CancellationToken ct, bool optional = false)
    {
        try
        {
            var records = new List<string>();
            await foreach (var record in git.ReadAsync(root, args, null, ct)) records.Add(record);
            var text = string.Join('\0', records);
            if (text.EndsWith('\n')) text = text[..^1]; // Remove Git's delimiter, never a pathname's own newline.
            return text.Length == 0 ? null : text;
        }
        catch (CallFailed e) when (optional && (e.Message.Contains("not a git repository", StringComparison.Ordinal)
            || e.Message.Contains("not a symbolic ref", StringComparison.Ordinal)))
        { return null; }
    }

    private static string Canonical(string path)
    {
        if (path is null) throw new CallBadArguments("Folder path is required.");
        if (path is "" or "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) throw new CallFailed("The home folder is unavailable.");
            path = path.Length <= 1 ? home : Path.Combine(home, path[2..]);
        }
        if (!Path.IsPathFullyQualified(path)) throw new CallFailed("Folder path must be absolute");
        try
        {
            var current = Path.GetPathRoot(path)!;
            foreach (var component in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".") continue;
                if (component == "..") { current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? current; continue; }
                var next = new DirectoryInfo(Path.Combine(current, component));
                current = next.ResolveLinkTarget(true)?.FullName ?? next.FullName;
                if (File.Exists(current)) throw new CallFailed("That path is a file. Choose a folder.");
                if (!Directory.Exists(current)) throw new CallFailed("That folder does not exist.");
            }
            if (File.Exists(current)) throw new CallFailed("That path is a file. Choose a folder.");
            if (!Directory.Exists(current)) throw new CallFailed("That folder does not exist.");
            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CallFailed("That folder does not exist."); }
    }
}
