using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests;

internal static class TestSessions
{
    /// <summary>Seeds an existing checkout for tests of provider work, independent of checkout preparation.</summary>
    internal static SessionInfo CreateReady(this SessionStore store, string agent, string repo)
    {
        var session = store.Create(agent, repo);
        Directory.CreateDirectory(store.Root(session)!);
        return session;
    }
}
