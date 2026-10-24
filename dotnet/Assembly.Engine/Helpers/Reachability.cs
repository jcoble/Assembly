using Assembly.Engine.Sessions;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Helpers;

/// <summary>
/// Who a session may message: its parent, its helpers, its siblings (its parent's other helpers), and the other
/// top-level sessions open on this machine now. A helper started from another machine reaches its parent there.
/// </summary>
public sealed class Reachability(ThreadStore threads, SessionRegistry sessions)
{
    public List<(ThreadInfo Thread, string Relation)> Of(ThreadInfo caller)
    {
        var reachable = new List<(ThreadInfo, string)>();
        if (caller.ParentThreadId is { } parentId)
        {
            // A parent on the machine that started this helper isn't a thread here; messages to it wait to be picked up.
            var parent = threads.Get(parentId) ?? new ThreadInfo(parentId, "", caller.RepoUrl, null, null, 0, Machine: "elsewhere");
            reachable.Add((parent, "parent"));
            reachable.AddRange(threads.Helpers(parentId).Where(sibling => sibling.ThreadId != caller.ThreadId).Select(sibling => (sibling, "sibling")));
        }
        reachable.AddRange(threads.Helpers(caller.ThreadId).Select(helper => (helper, "helper")));
        foreach (var id in sessions.OpenThreads)
        {
            if (id != caller.ThreadId && reachable.All(entry => entry.Item1.ThreadId != id) && threads.Get(id) is { ParentThreadId: null } open)
                reachable.Add((open, "session"));
        }
        return reachable;
    }
}
