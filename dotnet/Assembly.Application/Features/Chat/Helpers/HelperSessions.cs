using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>The existing parent-card writer used by the committed Engine helper projection.</summary>
public sealed class HelperSessions(SessionStore sessionStore, SessionRegistry sessions, LiveHub hub)
{
    public HelperRow Row { get; } = new(sessionStore, sessions, hub);
}
