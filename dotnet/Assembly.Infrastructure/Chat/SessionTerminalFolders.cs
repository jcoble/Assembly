using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat;

public static class SessionTerminalFolders
{
    /// <summary>
    /// A session's checkout folder, once it exists: what its terminal starts in. Fails for a session on another
    /// machine (a helper is on its parent's): its folder is there, and its terminal can't be reached from here yet.
    /// </summary>
    public static Func<string, string?> ExistingFolder(string checkouts, SessionStore sessions) => id =>
        sessions.Get(id) switch
        {
            { Machine: { Length: > 0 } machine } => throw new CallFailed($"The terminal for a session on {machine} can't be opened here yet."),
            { } session when sessions.Root(session) is { } folder && Directory.Exists(folder) => folder,
            null => throw new CallFailed($"Unknown terminal owner: {id}"),
            _ => null,
        };

}
