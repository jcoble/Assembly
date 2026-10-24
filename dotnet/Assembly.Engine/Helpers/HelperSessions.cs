using Assembly.Engine.Live;
using Assembly.Engine.Sessions;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Helpers;

/// <summary>
/// The Assembly MCP server's side, on one machine, put together: every session joins it with a token of its own
/// (<see cref="Tokens"/>), and its tools (<see cref="Tools"/>) let the agent start helper sessions of any agent here,
/// message the sessions it may reach, and read their answers. A helper is a thread of its own under its parent, shown
/// in the parent's thread as a subagent row (<see cref="Row"/>); a parent stays open while a helper of its is at work.
/// Messages are kept here, on the sending machine.
/// </summary>
public sealed class HelperSessions
{
    public HelperSessions(ThreadStore threads, SessionRegistry sessions, LiveHub hub, HelperLimits limits)
    {
        Row = new HelperRow(threads, sessions, hub);
        var reach = new Reachability(threads, sessions);
        Tools = new HelperToolList(threads,
        [
            new StartSessionTool(threads, sessions, Delivery, Row, limits),
            new SendMessageTool(threads, sessions, Delivery, reach, Row, limits),
            new MessageStatusTool(threads, sessions, hub),
            new ListSessionsTool(sessions, reach),
        ]);
    }

    public SessionTokens Tokens { get; } = new();
    public HelperDelivery Delivery { get; } = new();
    public HelperToolList Tools { get; }
    public HelperRow Row { get; }
}
