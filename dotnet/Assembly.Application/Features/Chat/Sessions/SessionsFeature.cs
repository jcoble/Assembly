using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The sessions over the socket (plan C1): the <c>sessions</c> topic, whose state is the newest
/// <see cref="ListLimit"/> sessions (or, with a key, that one), with an upsert for every change to any session, so one
/// that wakes up moves into the list; <c>sessions/page</c> for older ones; and <c>sessions/create</c>.
/// </summary>
internal sealed class SessionsFeature : IFeature
{
    public const string Topic = "sessions";

    /// <summary>The most sessions the topic's state or a page call reads.</summary>
    public const int ListLimit = 200;

    private readonly SessionStore _store;
    private readonly MachineSet _machines;

    public SessionsFeature(SessionStore store, MachineSet machines)
    {
        _store = store;
        _machines = machines;
    }

    public void Register(ICallRegistry calls)
    {
    }

    internal sealed record PageArgs(long? Before = null, int Limit = 50);

    internal sealed record Page(IReadOnlyList<SessionItem> Sessions, long? NextBefore);

    internal sealed record CreateArgs([property: JsonRequired] string Agent, [property: JsonRequired] string RepoUrl, string? Machine = null,
        string? ParentSessionId = null, string? Title = null);

    internal sealed record Created(string SessionId);
}
