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
internal sealed class FolderViews(IAppDatabase db, IProjectRoots? projects)
{
    /// <summary>The machine to forward to, or null for this one. A project this machine doesn't list was forwarded here.</summary>
    public string? RemoteMachine(FolderView view)
    {
        var project = projects?.Find(view.ProjectId);
        if (project is { Removed: true }) throw new CallFailed("That project is gone.", "source_changed");
        return project is null || project.Machine == "local" ? null : project.Machine;
    }

    /// <summary>The title of a session this machine is running in the folder, or in one inside or around it.</summary>
    public string? WorkingIn(string folder, CancellationToken ct) => db.QueryFirst("""
        SELECT title FROM engine_session
        WHERE machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
          AND (state IN ('running','needs_me') OR activity_state='running')
          AND (folder=$folder OR substr(folder,1,length($folder)+1)=$folder||'/' OR substr($folder,1,length(folder)+1)=folder||'/')
        ORDER BY updated_ms DESC LIMIT 1
        """, row => row.Text(0), ct, new SqlArg("$folder", folder));

    /// <summary>Nothing changes a folder a running session is working in, so its checkout is never disturbed.</summary>
    public void RequireWritable(string folder, CancellationToken ct, string consequence = "it is read-only here")
    {
        if (WorkingIn(folder, ct) is not { } title) return;
        var who = string.IsNullOrWhiteSpace(title) ? "Another session" : $"\"{title}\"";
        throw new CallFailed($"{who} is working in this folder, so {consequence}.", "folder_in_use");
    }
}
