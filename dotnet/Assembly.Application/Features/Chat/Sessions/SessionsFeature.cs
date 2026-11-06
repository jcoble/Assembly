using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Projects;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The sessions over the socket (plan C1): the <c>sessions</c> topic, whose state is the newest
/// <see cref="ListLimit"/> open (unsettled) sessions (or, with a key, that one), with an upsert for every change to any
/// session, settled or not, so one that wakes up moves into the list; <c>sessions/page</c> for older ones, all or one
/// group (<c>open</c> or <c>settled</c>: the window loads Settled, the user's archive, only once it's opened); <c>sessions/create</c>; and
/// <c>sessions/read</c>, which marks a session read up to a seq. Each item carries its summary from memory, so the
/// state, read under the Topics lock, reads no rows.
/// </summary>
internal sealed class SessionsFeature : IFeature
{
    public const string Topic = "sessions";

    /// <summary>The rail's invalidations: each says only which session changed.</summary>
    public const string RailTopic = "sessionRail";

    /// <summary>The most sessions the topic's state or a page call reads.</summary>
    public const int ListLimit = 200;

    private readonly SessionStore _store;
    private readonly MachineSet _machines;
    private readonly IRowSourceBindings? _sources;
    private readonly IProjectRoots? _projects;
    private readonly string? _owningMachine;
    private readonly string? _owningHome;
    private readonly Func<CreateArgs, CallContext, Task<Func<IWriteScope, Created>>>? _remoteCreate;
    private readonly Func<ByClientArgs, CallContext, Task<ByClientReply>>? _remoteByClient;
    private readonly IProjectFolders? _remoteFolders;

    public SessionsFeature(SessionStore store, MachineSet machines, IRowSourceBindings? sources = null,
        IProjectRoots? projects = null, string? owningMachine = null, string? owningHome = null,
        Func<CreateArgs, CallContext, Task<Func<IWriteScope, Created>>>? remoteCreate = null,
        Func<ByClientArgs, CallContext, Task<ByClientReply>>? remoteByClient = null, IProjectFolders? remoteFolders = null)
    {
        _store = store;
        _machines = machines;
        _sources = sources;
        _projects = projects;
        _owningMachine = owningMachine;
        _owningHome = owningHome;
        _remoteCreate = remoteCreate;
        _remoteByClient = remoteByClient;
        _remoteFolders = remoteFolders;
    }

    public void Register(ICallRegistry calls)
    {
        calls.Topic(Topic, SessionsJson.Default.SessionItem, session => session.SessionId, (key, call) =>
            key is null
                ? [.. _store.List(null, ListLimit, call.Cancellation, settled: false).Sessions.Select(_store.Item)]
                : _store.Get(key) is { } session ? [_store.Item(session)] : []);
        calls.Read(Topic, "page", SessionsJson.Default.PageArgs, SessionsJson.Default.Page, (args, call) =>
        {
            bool? settled = args.Group switch
            {
                null => null,
                "open" => false,
                "settled" => true,
                _ => throw new CallFailed("expected open or settled")
            };
            var page = _store.List(args.Before, Math.Clamp(args.Limit, 1, ListLimit), call.Cancellation, args.BeforeId, settled);
            return Task.FromResult(new Page([.. page.Sessions.Select(_store.Item)], page.NextBefore, page.NextBeforeId));
        });
        // The rail reads again on its state (at subscribe and each reconnect) and on every change, so the state is empty.
        calls.Topic(RailTopic, SessionsJson.Default.RailChange, change => change.SessionId, (_, _) => []);
        calls.Read(Topic, "rail", SessionsJson.Default.RailArgs, SessionsJson.Default.Rail, (args, call) =>
        {
            if ((args.Statuses ?? []).Any(status => status is not ("working" or "done" or "settled")))
                throw new CallFailed("expected working, done or settled");
            if (args.Sort is not ("recent" or "name"))
                throw new CallFailed("expected recent or name");
            if (args.Dir is not ("desc" or "asc"))
                throw new CallFailed("expected desc or asc");
            var bounded = args with { Limit = Math.Clamp(args.Limit, 1, ListLimit) };
            return Task.FromResult(_store.Rail(bounded, call.Cancellation));
        });
        // An explicit receipt captures the actual source before the one database write; legacy calls stay unqualified.
        calls.Write(Topic, "read", SessionsJson.Default.ReadArgs, SessionsJson.Default.Done, async (args, call) =>
        {
            if (args.ExpectedSource is not null && call.ExpectedSource is not null && args.ExpectedSource != call.ExpectedSource)
                throw new CallBadArguments("The session source arguments contradict the frame.");
            var expected = args.ExpectedSource ?? call.ExpectedSource;
            RowSourceBinding? binding = null;
            if (expected is not null)
            {
                if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
                if (_sources is null) throw new CallFailed("Source-bound session reads are unavailable.", "source_changed");
                binding = await _sources.CaptureAsync(expected, args.Session, call.Cancellation);
                call.SourceGuard = binding;
                call.Cancellation.ThrowIfCancellationRequested();
                binding.EnsureDisplayCurrent();
            }
            var session = _store.Get(args.Session);
            RequireFacade(session);
            if (session is null)
                throw new CallFailed("unknown session");
            return scope =>
            {
                if (binding is not null)
                {
                    call.Cancellation.ThrowIfCancellationRequested();
                    binding.EnsureDisplayCurrent(scope);
                    RequireFacade(_store.Get(scope, args.Session));
                }
                _store.MarkRead(scope, args.Session, args.Seq);
                return new SendFeature.Done(true);
            };

            void RequireFacade(SessionInfo? actual)
            {
                if (expected is not null && (actual is null || actual.SessionId != args.Session || actual.Machine != expected.RouteMachine))
                    throw new CallFailed("The selected session source changed.", "source_changed");
            }
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
        calls.Read(Topic, "byClient", SessionsJson.Default.ByClientArgs, SessionsJson.Default.ByClientReply, async (args, call) =>
        {
            RequireCreateOwner(call);
            await CaptureCreateSource(args.ExpectedSource, args.Machine, call);
            SessionStore.ValidateCreateClientId(args.ClientId);
            if (args.Machine is not null)
                return _remoteByClient is null ? throw new CallFailed("Remote creation is unavailable.", "configuration_unavailable")
                    : await _remoteByClient(args, call);
            call.SourceGuard?.EnsureCurrent();
            return _store.FindByClient(args.ClientId, call.Cancellation, _owningMachine!);
        });
        calls.Write(Topic, "create", SessionsJson.Default.CreateArgs, SessionsJson.Default.Created, async (args, call) =>
        {
            RequireCreateOwner(call);
            await CaptureCreateSource(args.ExpectedSource, args.Machine, call);
            SessionStore.ValidateCreateClientId(args.ClientId);
            if (args.Machine is not null)
                return _remoteCreate is null ? throw new CallFailed("Remote creation is unavailable.", "configuration_unavailable")
                    : await _remoteCreate(args, call);
            return await PrepareCreate(args, call);
        }, order: args => CreateOrder(args.ClientId, args.Machine));
        if (_remoteFolders is not null)
            calls.Write(Topic, "create_at_root", SessionsJson.Default.CreateAtRootArgs, SessionsJson.Default.Created, async (args, call) =>
            {
                RequireCreateOwner(call);
                if (args.TargetMachine != _owningMachine || string.IsNullOrWhiteSpace(args.FromMachine) ||
                    args.FromMachine == args.TargetMachine || args.Create is null || args.Create.Machine is not null ||
                    string.IsNullOrWhiteSpace(args.ProjectId) || args.ProjectId != args.Create.ProjectId ||
                    string.IsNullOrWhiteSpace(args.CanonicalRoot) || !Path.IsPathFullyQualified(args.CanonicalRoot))
                    throw new CallBadArguments("The prepared project descriptor is invalid.");
                await CaptureCreateSource(args.Create.ExpectedSource, null, call);
                SessionStore.ValidateCreateClientId(args.Create.ClientId);
                return await PrepareCreate(args.Create, call, async cancellation =>
                {
                    var inspected = await _remoteFolders.InspectAsync(args.CanonicalRoot, cancellation);
                    if (inspected.RootPath != args.CanonicalRoot)
                        throw new CallFailed("The project root changed.", "source_changed");
                    return new SessionCreationPreparation(args.TargetMachine, args.FromMachine, args.Create.ClientId,
                        args.ProjectId, inspected.RootPath);
                });
            }, order: args => CreateOrder(args.Create.ClientId, null));
    }

    private static string CreateOrder(string clientId, string? machine) => "session-create:" + machine + ":" + clientId;

    private async Task CaptureCreateSource(ExpectedSource? expected, string? machine, CallContext call)
    {
        if (expected is not null && call.ExpectedSource is not null && expected != call.ExpectedSource)
            throw new CallBadArguments("The session source arguments contradict the frame.");
        expected ??= call.ExpectedSource;
        if (expected is null) return;
        if (expected.RouteMachine != machine)
            throw new CallFailed("The selected create source changed.", "source_changed");
        if (_sources is null) throw new CallFailed("Source-bound creation is unavailable.", "source_changed");
        call.SourceGuard = await _sources.CaptureEngineAsync(expected, call.Cancellation);
        call.SourceGuard.EnsureCurrent();
    }

    private async Task<Func<IWriteScope, Created>> PrepareCreate(CreateArgs args, CallContext call,
        Func<CancellationToken, Task<SessionCreationPreparation>>? prepare = null)
    {
        call.SourceGuard?.EnsureCurrent();
        var known = _store.FindByClient(args.ClientId, call.Cancellation, _owningMachine!);
        if (known.SessionId is { } knownId)
            return scope =>
            {
                RequireCreateOwner(call);
                call.SourceGuard?.EnsureCurrent();
                var accepted = _store.FindByClient(scope, args.ClientId, _owningMachine!);
                if (accepted.SessionId != knownId) throw new CallFailed("The owning source changed.", "source_changed");
                return new Created(knownId, accepted.CheckoutOperationId, accepted.ProjectId, accepted.AcceptedRoot, accepted.Facts);
            };
        if (string.IsNullOrWhiteSpace(args.Agent)) throw new CallBadArguments("An agent is required.");
        using var stop = call.SourceGuard is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.SourceGuard.Changed);
        var token = stop?.Token ?? call.Cancellation;
        var preparation = prepare is null ? null : await prepare(token);
        var machine = _machines.Named(null) ?? throw new CallFailed("The local owner is unavailable.", "unavailable");
        var configured = (await machine.AgentsAsync(token)).Any(agent => agent.Id == args.Agent);
        return scope =>
        {
            RequireCreateOwner(call);
            call.SourceGuard?.EnsureCurrent();
            if (!ReferenceEquals(_machines.Named(null), machine)) throw new CallFailed("The owner changed.", "source_changed");
            var accepted = _store.FindByClient(scope, args.ClientId, _owningMachine!);
            if (accepted.SessionId is not null) return new Created(accepted.SessionId, accepted.CheckoutOperationId, accepted.ProjectId, accepted.AcceptedRoot, accepted.Facts);
            if (string.IsNullOrWhiteSpace(args.Agent) || !configured) throw new CallBadArguments("The agent is not configured on this owner.");
            var projectId = args.ProjectId;
            SessionLocation? parentLocation = null;
            if (args.ParentSessionId is not null)
            {
                var parent = _store.Get(scope, args.ParentSessionId);
                if (parent is null || parent.Machine is not null) throw new CallFailed("The parent is not owned here.", "wrong_machine");
                if (projectId is not null && projectId != parent.ProjectId) throw new CallBadArguments("A child inherits its parent's project.");
                projectId = parent.ProjectId;
                parentLocation = _store.OwningLocation(scope, args.ParentSessionId);
                if (parentLocation is null || !parentLocation.Ready) throw new CallFailed("The parent's checkout is not ready.", "checkout_not_ready");
            }
            var folder = parentLocation?.Folder ?? preparation?.CanonicalRoot ?? BareSessionFolder.ForHome(_owningHome);
            if (projectId is not null && preparation is null)
            {
                var project = _projects?.Find(scope, projectId);
                if (project is null || project.Removed || project.Machine != "local")
                    throw new CallFailed("The selected project is unavailable on this owner.", "wrong_machine");
                if (parentLocation is null) folder = project.RootPath;
            }
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                throw new CallFailed("The owner's home is unavailable.", "configuration_unavailable");
            var session = _store.Create(scope, args.Agent, args.ClientId, folder, projectId,
                args.Checkout ?? (projectId is null ? null : new CheckoutIntent("current", null, null)),
                args.ParentSessionId, args.ParentSessionId is null ? args.Title : args.Title ?? "Session",
                initialConfig: args.InitialConfig, preparation: preparation);
            var committed = _store.FindByClient(scope, args.ClientId, _owningMachine!);
            return new Created(session.SessionId, committed.CheckoutOperationId, committed.ProjectId, committed.AcceptedRoot, committed.Facts);
        };
    }

    private void RequireCreateOwner(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        call.Closed.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_owningMachine))
            throw new CallFailed("The actual owning Engine is unavailable.", "configuration_unavailable");
    }

    /// <summary>
    /// A page after the cursor: <c>before</c> and <c>beforeId</c> are the last page's <c>nextBefore</c> and
    /// <c>nextBeforeId</c>; <c>group</c> is <c>open</c>, <c>settled</c> or left out for all.
    /// </summary>
    internal sealed record PageArgs(long? Before = null, int Limit = 50, string? BeforeId = null, string? Group = null);

    internal sealed record Page(IReadOnlyList<SessionItem> Sessions, long? NextBefore, string? NextBeforeId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record CreateArgs([property: JsonRequired] string Agent, [property: JsonRequired] string ClientId,
        string? ProjectId = null, string? Machine = null, CheckoutIntent? Checkout = null,
        string? ParentSessionId = null, string? Title = null, InitialSessionConfig? InitialConfig = null,
        ExpectedSource? ExpectedSource = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record ByClientArgs([property: JsonRequired] string ClientId, string? Machine = null, ExpectedSource? ExpectedSource = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record CreateAtRootArgs([property: JsonRequired] string TargetMachine, [property: JsonRequired] string FromMachine,
        [property: JsonRequired] CreateArgs Create, [property: JsonRequired] string ProjectId, [property: JsonRequired] string CanonicalRoot);
    internal sealed record ByClientReply(string? SessionId, string? CheckoutState, string? State, string? CheckoutOperationId = null, string? ProjectId = null, string? AcceptedRoot = null, CreatedSessionFacts? Facts = null);

    internal sealed record CreatedSessionFacts(string Machine, string Agent, string? ParentSessionId,
        string Folder, bool ManagedCheckout, string RepoUrl, bool IsBare = false);

    internal sealed record Created(string SessionId, string? CheckoutOperationId = null, string? ProjectId = null, string? AcceptedRoot = null, CreatedSessionFacts? Facts = null);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] long Seq,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record MarkArgs([property: JsonRequired] string Session, [property: JsonRequired] string State);

    /// <summary>Filters and view options, with a cursor or the row to keep in view and how many rows sit above it.</summary>
    internal sealed record RailArgs(IReadOnlyList<string>? Providers = null, IReadOnlyList<string>? Locations = null,
        IReadOnlyList<string>? Statuses = null, RailGroupBy? GroupBy = null, string Sort = "recent", string Dir = "desc",
        IReadOnlyList<string>? Folded = null, string? Filter = null, RailCursor? After = null, int Limit = 50,
        string? Anchor = null, int Above = 0);

    internal sealed record RailGroupBy(bool Status, bool Project);

    /// <summary>The last row's fixed section/group/header position and SQL ordering key.</summary>
    internal sealed record RailCursor(int Section, string Group, string RepoUrl, int Header, string Ordering);

    internal sealed record RailTotals(int Needs);

    /// <summary>
    /// One session's own state and SQL order. <c>Preview</c> is its newest assistant text, cut to 160 characters.
    /// <c>Source</c> is recorded identity for resolution, not a live source binding or native-history witness.
    /// <c>NativeActivity</c> says native subagents are recorded on this session, so its row has something to open.
    /// </summary>
    internal sealed record RailEntry(string SessionId, string Kind, string Section, string Agent, string RepoUrl, string Group,
        int Count, IReadOnlyList<string> Machines, string? Title, string? Machine, long UpdatedAt, long? CompletedAt,
        bool Running, bool Failed, bool Unread, string? Branch,
        string Icon, int SectionIndex, int Header, string Ordering, string? Preview,
        string? ParentSessionId, int Depth, long? PinnedAt, long? OrchestratorAt, long? Position, string? Model,
        string? Worktree, string? LastError, string? ResumeCommand, long? LastSeenMs, ExpectedSource? Source, bool NativeActivity);

    internal sealed record Rail(RailTotals Totals, IReadOnlyList<string> Agents, IReadOnlyList<RailEntry> Sessions, RailCursor? Next, int Start = 0);

    /// <summary>A committed change to a session, parent or helper, on the <c>sessionRail</c> topic: read the rail again.</summary>
    internal sealed record RailChange(string SessionId);
}
