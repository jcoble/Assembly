using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat;

/// <summary>
/// Turns a path relative to a session's root folder into a full one, refusing anything outside the root. "" and "."
/// are the root itself, for calls that work on the whole folder (git, worktrees). A path through a link is refused
/// wherever the link points, so nothing reaches past the root and no loop is followed.
/// </summary>
public static class SessionPaths
{
    public static string RootOf(SessionStore sessions, string sessionId)
    {
        if (FolderView.IsView(sessionId)) return FolderView.Parse(sessionId).LocalFolder();
        var session = sessions.Get(sessionId) ?? throw new CallFailed("unknown session");
        if (!string.IsNullOrEmpty(session.Machine))
            throw new CallFailed($"this session's files are on {session.Machine}, which this window can't show yet");
        var root = sessions.Root(session) ?? throw new CallFailed("this session has no folder");
        return Directory.Exists(root) ? root : throw new CallFailed("this session's folder is made on its first message");
    }

    /// <summary>Files-specific contract; shared Git/worktree root resolution keeps its existing semantics.</summary>
    public static string ResolveFile(string root, string relative, bool allowRoot = false)
    {
        if (allowRoot && relative == ".") return Resolve(root, "");
        if (relative.Length > 4096 || relative.Contains('\0') || Path.IsPathRooted(relative))
            throw new CallFailed("expected a relative file path of at most 4096 characters");
        if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part is "." or ".."))
            throw new CallFailed("that path is outside the session's folder: traversal components are not allowed");
        if (relative.Length == 0 && !allowRoot)
            throw new CallFailed("the session's root cannot be changed as a file");
        return Resolve(root, relative);
    }

    public static string Resolve(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.IsPathRooted(relative))
            throw new CallFailed("expected a path relative to the session's folder");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative)));
        if (full == root)
            return root;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CallFailed("that path is outside the session's folder");
        for (var at = full; at != root; at = Path.GetDirectoryName(at)!)
        {
            if (new FileInfo(at).LinkTarget is not null)
                throw new CallFailed("that path goes through a link");
        }
        return full;
    }
}
