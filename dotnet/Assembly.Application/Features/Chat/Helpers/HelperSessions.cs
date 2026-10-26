using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// The Assembly MCP server's side, on one machine, put together: every session joins it with a token of its own
/// (<see cref="Tokens"/>), and its tools (<see cref="Tools"/>) let the agent start helper sessions of any agent here,
/// message the sessions it may reach, and read their answers. A helper is a session of its own under its parent, shown
/// in the parent's session as a subagent row (<see cref="Row"/>); a parent stays open while a helper of its is at work.
/// Messages are kept here, on the sending machine.
/// </summary>
public sealed class HelperSessions
{
    public HelperSessions(SessionStore sessionStore, SessionRegistry sessions, LiveHub hub, HelperLimits limits)
    {
        Row = new HelperRow(sessionStore, sessions, hub);
        var reach = new Reachability(sessionStore, sessions);
        Tools = new HelperToolList(sessionStore,
        [
            new StartSessionTool(sessionStore, sessions, Delivery, Row, limits),
            new SendMessageTool(sessionStore, sessions, Delivery, reach, Row, limits),
            new MessageStatusTool(sessionStore, sessions, hub),
            new ListSessionsTool(sessions, reach),
        ]);
    }

    public SessionTokens Tokens { get; } = new();
    public HelperDelivery Delivery { get; } = new();
    public HelperToolList Tools { get; }
    public HelperRow Row { get; }
}
