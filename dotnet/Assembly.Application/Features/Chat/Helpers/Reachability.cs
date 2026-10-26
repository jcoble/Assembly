using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// Who a session may message: its parent, its helpers, its siblings (its parent's other helpers), and the other
/// top-level sessions open on this machine now. A helper started from another machine reaches its parent there.
/// </summary>
public sealed class Reachability(SessionStore sessionStore, SessionRegistry sessions)
{
    public List<(SessionInfo Session, string Relation)> Of(SessionInfo caller)
    {
        var reachable = new List<(SessionInfo, string)>();
        if (caller.ParentSessionId is { } parentId)
        {
            // A parent on the machine that started this helper isn't a session here; messages to it wait to be picked up.
            var parent = sessionStore.Get(parentId) ?? new SessionInfo(parentId, "", caller.RepoUrl, null, null, 0, Machine: "elsewhere");
            reachable.Add((parent, "parent"));
            reachable.AddRange(sessionStore.Helpers(parentId).Where(sibling => sibling.SessionId != caller.SessionId).Select(sibling => (sibling, "sibling")));
        }
        reachable.AddRange(sessionStore.Helpers(caller.SessionId).Select(helper => (helper, "helper")));
        foreach (var id in sessions.OpenSessions)
        {
            if (id != caller.SessionId && reachable.All(entry => entry.Item1.SessionId != id) && sessionStore.Get(id) is { ParentSessionId: null } open)
                reachable.Add((open, "session"));
        }
        return reachable;
    }
}
