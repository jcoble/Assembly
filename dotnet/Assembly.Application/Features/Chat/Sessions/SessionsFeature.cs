using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The sessions over the socket (plan C1): the <c>sessions</c> topic, whose state is the newest
/// <see cref="ListLimit"/> sessions (or, with a key, that one), with an upsert for every change to any session, so one
/// that wakes up moves into the list; <c>sessions/page</c> for older ones; <c>sessions/create</c>; and
/// <c>sessions/read</c>, which marks a session read up to a seq. Each item carries its summary from memory, so the
/// state, read under the Topics lock, reads no rows.
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
        calls.Topic(Topic, SessionsJson.Default.SessionItem, session => session.SessionId, (key, call) =>
            key is null
                ? [.. _store.List(null, ListLimit, call.Cancellation).Sessions.Select(_store.Item)]
                : _store.Get(key) is { } session ? [_store.Item(session)] : []);
        calls.Read(Topic, "page", SessionsJson.Default.PageArgs, SessionsJson.Default.Page, (args, call) =>
        {
            var page = _store.List(args.Before, Math.Clamp(args.Limit, 1, ListLimit), call.Cancellation, args.BeforeId);
            return Task.FromResult(new Page([.. page.Sessions.Select(_store.Item)], page.NextBefore, page.NextBeforeId));
        });
        // Only the database: no order key, as nothing outside it can hang.
        calls.Write(Topic, "read", SessionsJson.Default.ReadArgs, SessionsJson.Default.Done, (args, _) =>
        {
            if (_store.Get(args.Session) is null)
                throw new CallFailed("unknown session");
            return Task.FromResult<Func<IWriteScope, SendFeature.Done>>(scope =>
            {
                _store.MarkRead(scope, args.Session, args.Seq);
                return new SendFeature.Done(true);
            });
        });
        calls.Write(Topic, "mark", SessionsJson.Default.MarkArgs, SessionsJson.Default.Done, (args, _) =>
        {
            if (_store.Get(args.Session) is null)
                throw new CallFailed("unknown session");
            if (args.State is not ("working" or "done" or "settled"))
                throw new CallFailed("expected working, done or settled");
            return Task.FromResult<Func<IWriteScope, SendFeature.Done>>(scope =>
            {
                _store.Mark(scope, args.Session, args.State);
                return new SendFeature.Done(true);
            });
        });
        // The machine's work runs before the write, which only answers: the store's own write publishes the new
        // session, so it goes out on this socket before the answer. It's keyed on its machine, so one that hangs holds
        // up only creates there.
        calls.Write(Topic, "create", SessionsJson.Default.CreateArgs, SessionsJson.Default.Created, async (args, call) =>
        {
            if (args.Agent.Length == 0 || args.RepoUrl.Length == 0)
                throw new CallFailed("expected an agent and a repoUrl");
            var machine = _machines.Named(args.Machine) ?? throw new CallFailed("unknown machine");
            if (!(await machine.AgentsAsync(call.Cancellation)).Any(known => known.Id == args.Agent))
                throw new CallFailed($"this machine has no agent {args.Agent}");
            var session = args.ParentSessionId is { } parentId
                ? await machine.CreateHelperAsync(new SessionInfo(parentId, args.Agent, args.RepoUrl, null, null, 0), args.Agent,
                    args.Title ?? "Helper", call.Cancellation)
                : await machine.CreateSessionAsync(args.Agent, args.RepoUrl, call.Cancellation);
            return _ => new Created(session.SessionId);
        }, args => $"machine {args.Machine}");
    }

    /// <summary>A page after the cursor: <c>before</c> and <c>beforeId</c> are the last page's <c>nextBefore</c> and <c>nextBeforeId</c>.</summary>
    internal sealed record PageArgs(long? Before = null, int Limit = 50, string? BeforeId = null);

    internal sealed record Page(IReadOnlyList<SessionItem> Sessions, long? NextBefore, string? NextBeforeId);

    internal sealed record CreateArgs([property: JsonRequired] string Agent, [property: JsonRequired] string RepoUrl, string? Machine = null,
        string? ParentSessionId = null, string? Title = null);

    internal sealed record Created(string SessionId);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] long Seq);

    internal sealed record MarkArgs([property: JsonRequired] string Session, [property: JsonRequired] string State);
}
