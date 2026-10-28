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
        var session = sessions.Get(sessionId) ?? throw new CallFailed("unknown session");
        if (session.Machine is not null)
            throw new CallFailed($"this session's files are on {session.Machine}, which this window can't show yet");
        var root = sessions.Root(session) ?? throw new CallFailed("this session has no folder");
        return Directory.Exists(root) ? root : throw new CallFailed("this session's folder is made on its first message");
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
