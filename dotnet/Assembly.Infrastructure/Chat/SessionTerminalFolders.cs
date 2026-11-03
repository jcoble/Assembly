using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat;

public static class SessionTerminalFolders
{
    /// <summary>Only an owning session's ready, recorded directory may host its terminal; never creates a checkout.</summary>
    public static Func<string, string?> ExistingFolder(string checkouts, SessionStore sessions) => id =>
    {
        var session = sessions.Get(id) ?? throw new CallFailed($"Unknown terminal owner: {id}");
        if (session.Machine is { Length: > 0 } machine)
            throw new CallFailed($"The terminal for a session on {machine} cannot be opened here.");
        var location = sessions.Location(session);
        if (location is null || !location.Ready)
            throw new CallFailed($"The terminal folder for session {id} is not ready: {location?.Folder ?? "not materialized"}.");
        if (!Path.IsPathFullyQualified(location.Folder) || !Directory.Exists(location.Folder))
            throw new CallFailed($"The terminal folder for session {id} is unavailable: {location.Folder}.");
        return location.Folder;
    };
}
