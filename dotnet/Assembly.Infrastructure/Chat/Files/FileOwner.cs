using System.Runtime.InteropServices;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.Sessions;
using Microsoft.Win32.SafeHandles;

namespace Assembly.Infrastructure.Chat.Files;

public sealed record FileOwnership(string Machine, string CheckoutRoot, string RemoteSessionId, string? File = null);

/// <summary>Resolves trusted ownership for a file call. A session target takes the session's trusted root and a relative
/// path; caller paths never select its root. A path target takes a machine and an absolute path, and is the only place a
/// caller path names a file: after <c>~</c> expansion on this machine it follows links to the real path and accepts any
/// regular file this backend's user can read, refusing NUL, <c>.</c>/<c>..</c>, over 4096 characters, <c>/proc</c>,
/// <c>/sys</c>, <c>/dev</c> and anything not a regular file. It returns the owner as (this machine, real parent folder,
/// file name).</summary>
internal sealed class FileOwner(SessionStore sessions, IAppDatabase db, IProjectRoots? projects = null)
{
    public FileOwnership Resolve(string sessionId, CancellationToken ct, FileOwnership? expected = null)
    {
        using var scope = db.OpenSnapshot(ct);
        var local = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0))
            ?? throw new CallFailed("the owning engine identity is unavailable");
        if (FolderView.IsView(sessionId)) return Match(OfView(scope, local, sessionId), expected);
        var session = sessions.Get(scope, sessionId) ?? throw new CallFailed("unknown session");
        var engine = scope.QueryFirst("SELECT folder,holder,state FROM engine_session WHERE machine=$machine AND id=$id",
            r => new { Folder = r.Text(0), Holder = r.Text(1), State = r.Text(2) }, new("$machine", local), new("$id", sessionId));
        var remote = !string.IsNullOrEmpty(session.Machine) ? session.Machine :
            engine is { State: "foreign" } && engine.Holder != "local" ? engine.Holder : null;
        if (remote is null)
        {
            if (engine is not null && (engine.Holder != "local" || engine.State == "foreign"))
                throw new CallFailed("this session's checkout is not owned by this engine");
            var root = engine?.Folder ?? sessions.Root(scope, session) ?? throw new CallFailed("this session has no folder");
            var resolved = Match(new FileOwnership(local, root, sessionId), expected);
            if (!Directory.Exists(root)) throw new CallFailed("this session's folder is made on its first message");
            return resolved;
        }
        var owners = scope.Query("""
            SELECT machine FROM engine_mirror WHERE (machine=$remote OR name=$remote) AND state='live' LIMIT 2
            """, r => r.Text(0), new SqlArg("$remote", remote));
        if (owners.Count != 1) throw new CallFailed($"this session's files are on {remote}; the owning machine is disconnected or its identity is unavailable");
        var folder = scope.QueryFirst("""
            SELECT json_extract(r.row_json,'$.folder') FROM engine_mirror_row r JOIN engine_mirror m
              ON m.machine=r.machine AND m.generation=r.generation
            WHERE r.machine=$machine AND r.entity_kind='session' AND r.entity_id=$id AND m.state='live'
              AND json_extract(r.row_json,'$.holder')='local' AND json_extract(r.row_json,'$.state')<>'foreign'
            """, r => r.IsNull(0) ? null : r.Text(0), new("$machine", owners[0]), new("$id", sessionId));
        if (string.IsNullOrEmpty(folder)) throw new CallFailed("the owning machine has not published this session's checkout");
        return Match(new FileOwnership(owners[0], folder, sessionId), expected);
    }

    /// <summary>A viewed folder is owned by its project's machine.</summary>
    private FileOwnership OfView(IReadScope scope, string local, string sessionId)
    {
        var view = FolderView.Parse(sessionId);
        if (new FolderViews(db, projects).RemoteMachine(view) is not { } name)
            return new FileOwnership(local, view.LocalFolder(), sessionId);
        var owners = scope.Query("SELECT machine FROM engine_mirror WHERE name=$name AND state='live' LIMIT 2",
            r => r.Text(0), new SqlArg("$name", name));
        if (owners.Count != 1) throw new CallFailed($"This folder is on {name}; the machine is disconnected.");
        return new FileOwnership(owners[0], view.Folder, sessionId);
    }

    /// <summary>A viewed folder a running session works in takes no file changes here.</summary>
    public void RequireViewWritable(string sessionId, CancellationToken ct) => new FolderViews(db, projects)
        .RequireWritable(FolderView.Parse(sessionId).LocalFolder(), ct, "this file can't be saved here");

    /// <summary>What a remote viewed folder's calls go through: there's no session row to bring a source from.</summary>
    public ExpectedSource? ViewSource(string machine, CancellationToken ct) => db.QueryFirst(
        "SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'",
        r => new ExpectedSource(r.Text(0), r.Long(1), r.Text(2)), ct, new SqlArg("$machine", machine));

    internal FileOwnership ResolvePath(PathTarget target, CancellationToken ct, FileOwnership? expected = null)
    {
        if (target.Machine is not null) throw new CallFailed("this machine's files must be opened on it");
        var real = RealFile(target.Path);
        var local = db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0), ct)
            ?? throw new CallFailed("the owning engine identity is unavailable");
        var actual = Inside(real, target.InsideOf, local, ct)
            ?? new FileOwnership(local, Path.GetDirectoryName(real)!, "", Path.GetFileName(real));
        if (expected is not null && expected != actual)
            throw new CallFailed("the file moved; the draft is preserved", "checkout_changed");
        return actual;
    }

    /// <summary>The file's real absolute path, or the refusal that says why a caller's path can't name a file.</summary>
    internal static string RealFile(string path)
    {
        if (path is null || path.Contains('\0') || path.Length > MaxPath)
            throw new CallFailed("That file link isn't a file path.", "not_a_path");
        var full = ExpandHome(path);
        if (!Path.IsPathFullyQualified(full) || full.Split('/').Any(part => part is "." or ".."))
            throw new CallFailed("That file link isn't a full path.", "not_full_path");
        try
        {
            var real = RealPath(full);
            if (Unreadable.Any(folder => real == folder || real.StartsWith(folder + '/', StringComparison.Ordinal))) throw NotRegular();
            RequireRegular(real);
            return real;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CallFailed("that file can't be read"); }
    }

    /// <summary>The named session's ownership when the real path lies inside its checkout. The session is only a hint: if it
    /// can't be resolved here, for any reason, the file is simply outside.</summary>
    private FileOwnership? Inside(string real, string? insideOf, string local, CancellationToken ct)
    {
        if (insideOf is null) return null;
        try
        {
            var owner = Resolve(insideOf, ct);
            if (owner.Machine != local) return null;
            var root = RealPath(owner.CheckoutRoot);
            var prefix = root.EndsWith('/') ? root : root + '/';
            return real.StartsWith(prefix, StringComparison.Ordinal) ? owner with { File = real[prefix.Length..] } : null;
        }
        catch (Exception e) when (e is CallFailed or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ExpandHome(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal)) return path;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) throw new CallFailed("The home folder is unavailable.");
        return path.Length <= 2 ? home : Path.Join(home, path[2..]);
    }

    /// <summary>Follows every link, one step at a time, so a link's own target is checked for links too. A component that
    /// doesn't exist stays as written; the regular-file check then says the file isn't there.</summary>
    private static string RealPath(string path, int depth = 0)
    {
        if (depth > MaxLinks) throw new CallFailed("that path has too many links");
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var link = new FileInfo(next).LinkTarget;
            current = link is null ? next : RealPath(Path.GetFullPath(link, current), depth + 1);
        }
        return current;
    }

    /// <summary>Opens without blocking, since opening a FIFO blocks until a writer appears. Sockets fail the open; FIFOs and
    /// most devices open but can't seek.</summary>
    private static void RequireRegular(string real)
    {
        if (Directory.Exists(real)) throw NotRegular();
        var fd = open(real, OperatingSystem.IsMacOS() ? MacReadNonBlocking : LinuxReadNonBlocking);
        if (fd < 0)
            throw Marshal.GetLastPInvokeError() switch
            {
                NoSuchFile or NotAFolder => new CallFailed("that file isn't there"),
                AccessDenied => new CallFailed("that file can't be read"),
                _ => NotRegular(),
            };
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        if (!stream.CanSeek) throw NotRegular();
    }

    private static CallFailed NotRegular() => new("That isn't a regular file, so it can't open.", "not_regular");

    private const int MaxPath = 4096;
    private const int MaxLinks = 40;
    private const int NoSuchFile = 2; // ENOENT, the same on Linux and macOS.
    private const int NotAFolder = 20; // ENOTDIR: a file named as if it were a folder; the same on Linux and macOS.
    private const int AccessDenied = 13; // EACCES, the same on Linux and macOS.
    private const int LinuxReadNonBlocking = 0x800 | 0x80000; // O_RDONLY | O_NONBLOCK | O_CLOEXEC
    private const int MacReadNonBlocking = 0x4 | 0x1000000; // O_RDONLY | O_NONBLOCK | O_CLOEXEC
    private static readonly string[] Unreadable = ["/proc", "/sys", "/dev"];

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static FileOwnership Match(FileOwnership actual, FileOwnership? expected)
    {
        if (expected is not null && expected != actual)
            throw new CallFailed("The session's checkout changed. The existing draft is preserved.", "checkout_changed");
        return actual;
    }

    public bool IsLocal(FileOwnership ownership, CancellationToken ct) => ownership.Machine ==
        db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0), ct);
}
