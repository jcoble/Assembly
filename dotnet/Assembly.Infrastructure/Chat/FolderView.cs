using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Projects;

namespace Assembly.Infrastructure.Chat;

/// <summary>
/// Another project's folder, opened in the Files and Git tabs without changing the session. It travels in a call's
/// session field as an opaque id, because every Git and file call is already keyed by that field, and never reaches
/// the session store or the Engine. It names a project and a folder; the machine comes from the project.
/// </summary>
public sealed record FolderView(string ProjectId, string Folder)
{
    private const string Prefix = "view:";

    public static bool IsView(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static FolderView Parse(string id)
    {
        var parts = id[Prefix.Length..].Split(':');
        try
        {
            if (parts.Length == 2) return new(Text(parts[0]), Text(parts[1]));
        }
        catch (FormatException) { }
        throw new CallBadArguments("That isn't a folder view.");
    }

    public static string Id(string projectId, string folder) => Prefix + Hex(projectId) + ":" + Hex(folder);

    /// <summary>The folder on this machine, refused unless it's a whole absolute path that exists now.</summary>
    public string LocalFolder()
    {
        if (!Path.IsPathFullyQualified(Folder) || Folder.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new CallBadArguments("That isn't a folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder));
        return Directory.Exists(full) ? full : throw new CallFailed("Checkout/Worktree deleted.", "checkout_unavailable");
    }

    private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();
    private static string Text(string hex) => Encoding.UTF8.GetString(Convert.FromHexString(hex));
}

/// <summary>Where a viewed folder lives, and whether it may be changed here.</summary>
internal sealed class FolderViews(IAppDatabase db, IProjectRoots? projects, StringComparison? paths = null)
{
    /// <summary>macOS and Windows disks treat differently cased paths as one folder.</summary>
    private readonly StringComparison _paths = paths ??
        (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The machine to forward to, or null for this one. A project this machine doesn't list was forwarded here.</summary>
    public string? RemoteMachine(FolderView view)
    {
        var project = projects?.Find(view.ProjectId);
        if (project is { Removed: true }) throw new CallFailed("That project is gone.", "source_changed");
        return project is null || project.Machine == "local" ? null : project.Machine;
    }

    /// <summary>The title of a session this machine is running in the folder, or in one inside or around it.</summary>
    public string? WorkingIn(string folder, CancellationToken ct)
    {
        // SQL picks the few active sessions; only the disk can say whether two paths are the same folder.
        var active = db.Query("""
            SELECT COALESCE(title,''),folder FROM engine_session
            WHERE machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
              AND (state IN ('running','needs_me') OR activity_state='running')
            ORDER BY updated_ms DESC
            """, row => (Title: row.Text(0), Folder: row.Text(1)), ct);
        var wanted = Resolved(folder);
        foreach (var session in active)
            if (Overlap(wanted, Resolved(session.Folder), _paths)) return session.Title;
        return null;
    }

    /// <summary>The path with every symlink along it followed; a part that isn't there yet stays as written.</summary>
    private static string Resolved(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (Path.Exists(current)) current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>Whether two folders are the same one, or one holds the other.</summary>
    internal static bool Overlap(string a, string b, StringComparison paths) =>
        string.Equals(a, b, paths) || a.StartsWith(b + '/', paths) || b.StartsWith(a + '/', paths);

    /// <summary>Nothing changes a folder a running session is working in, so its checkout is never disturbed.</summary>
    public void RequireWritable(string folder, CancellationToken ct, string consequence = "it is read-only here")
    {
        if (WorkingIn(folder, ct) is not { } title) return;
        var who = string.IsNullOrWhiteSpace(title) ? "Another session" : $"\"{title}\"";
        throw new CallFailed($"{who} is working in this folder, so {consequence}.", "folder_in_use");
    }
}
