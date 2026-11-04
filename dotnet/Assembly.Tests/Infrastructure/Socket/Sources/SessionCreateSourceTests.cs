using Assembly.Application.Common.Projects;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Projects;
using System.Runtime.CompilerServices;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionCreateSourceTests
{
    [Fact]
    public async Task Closed_owner_ingress_rejects_wrong_target_from_root_and_project_before_any_effect()
    {
        await using var setup = new Setup(); await setup.Start();
        var host = setup.Hosts.Remote;
        using var socket = await Open(host);
        var original = Descriptor(setup, "rejected_client");
        var wrongTarget = original with { TargetMachine = setup.Hosts.Mac.Runtime.Machine };
        var sameFrom = original with { FromMachine = original.TargetMachine };
        var missingFrom = original with { FromMachine = "" };
        var wrongProject = original with { ProjectId = "different-project" };
        var sessions = host.Fixture.Count("engine_session");
        var listed = host.Sessions.List(null, 20).Sessions.Select(session => session.SessionId).ToArray();
        var effects = host.Fixture.Count("engine_outbox");
        foreach (var descriptor in new[] { wrongTarget, sameFrom, missingFrom, wrongProject })
            Error(await Call(socket, "create_at_root", descriptor), "bad_args");
        // Root authority is private to the prepared descriptor, never a public CreateArgs field.
        Error(await Raw(socket, "create", JsonSerializer.Serialize(new
        {
            agent = "claude",
            clientId = "ui_root",
            workingFolder = setup.Root.FullName,
            expectedSource = new ExpectedSource(host.Runtime.Machine, 0, null)
        })), "bad_args");
        Error(await Raw(socket, "create_at_root", JsonSerializer.Serialize(new
        {
            original.TargetMachine,
            original.FromMachine,
            original.ProjectId,
            original.CanonicalRoot,
            create = new
            {
                agent = "claude",
                clientId = "nested_root",
                projectId = original.ProjectId,
                workingFolder = setup.Root.FullName
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web))), "bad_args");
        Assert.Equal(sessions, host.Fixture.Count("engine_session"));
        Assert.Equal(effects, host.Fixture.Count("engine_outbox"));
        Assert.Equal(listed, host.Sessions.List(null, 20).Sessions.Select(session => session.SessionId));
        Assert.Empty(setup.RemoteMachine.Asked);
        Assert.Equal(0, setup.Folders.Inspections);
        Assert.Equal(0, setup.Git.Reads);
        Assert.Null(setup.Projects.Get(setup.Project.Id)!.LastUsedAt);
        Assert.Equal(0, setup.Launcher.Launches);
    }

    [Fact]
    public async Task Original_remote_receipt_replaced_before_server_capture_cannot_create_or_reconcile()
    {
        await using var setup = new Setup(); await setup.Start();
        await setup.Hosts.StartMirror();
        var old = await ReadySource();
        await setup.Hosts.DisconnectMirror();
        await setup.Hosts.ReplaceAtSameEndpoint(new TwoHosts.Host(windowCalls: true));
        await setup.Hosts.ReconnectSaved();
        await setup.Hosts.Reached(setup.Hosts.Remote.Through);
        var current = await ReadySource();
        Assert.NotEqual(old.Machine, current.Machine);
        Assert.Equal(old.RouteMachine, current.RouteMachine);
        using var socket = await Open(setup.Hosts.Mac);
        var count = setup.Hosts.Mac.Fixture.Count("engine_session");
        var effects = setup.Hosts.Mac.Fixture.Count("engine_outbox");
        Error(await Call(socket, "create", new SessionsFeature.CreateArgs("claude", "old_capture",
            Machine: "workbox-test", ExpectedSource: old)), "source_changed");
        Error(await Call(socket, "byClient", new SessionsFeature.ByClientArgs("old_capture", "workbox-test", old)), "source_changed");
        Assert.Equal(count, setup.Hosts.Mac.Fixture.Count("engine_session"));
        Assert.Equal(effects, setup.Hosts.Mac.Fixture.Count("engine_outbox"));
        Assert.Equal(0, setup.Forwards);
        Assert.Empty(setup.LocalMachine.Asked);
        Assert.Empty(setup.RemoteMachine.Asked);
        Assert.Equal(0, setup.Folders.Inspections);
        Assert.Equal(0, setup.Launcher.Launches);

        async Task<ExpectedSource> ReadySource()
        {
            var owned = Assert.Single(setup.Hosts.Connections.CaptureMirrorOwners());
            var mirror = owned.Mirror;
            var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Ready() => caughtUp.TrySetResult();
            mirror.CaughtUp += Ready;
            try
            {
                var live = await mirror.CaptureLiveBindingAsync(default);
                if (live is null)
                {
                    await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    live = await mirror.CaptureLiveBindingAsync(default);
                }
                Assert.NotNull(live);
                var source = Source(setup.Hosts);
                Assert.Equal(live.Machine, source.Machine);
                Assert.Equal(live.Generation, source.Generation);
                var captured = await setup.Hosts.Connections.CaptureSessionOpenSourceAsync(setup.Hosts.Mac.Fixture.Db, source, default);
                Assert.Same(owned.Machine, captured.Machine);
                Assert.Equal(source.Machine, live.OwnerBinding.Machine);
                Assert.Same(live, captured.Live);
                captured.EnsureCurrent();
                return source;
            }
            finally { mirror.CaughtUp -= Ready; }
        }
    }

    [Fact]
    public async Task Accepted_owner_client_replay_keeps_original_project_without_discovery_touch_or_new_session()
    {
        await using var setup = new Setup(); await setup.Start();
        var host = setup.Hosts.Remote;
        using var socket = await Open(host);
        var first = Result(await Call(socket, "create_at_root", Descriptor(setup, "accepted_owner")));
        var accepted = first.GetProperty("sessionId").GetString()!;
        Assert.Equal(setup.Project.Id, first.GetProperty("projectId").GetString());
        Assert.True(setup.Folders.Inspections > 0);
        Assert.Contains("agents", setup.RemoteMachine.Asked);
        setup.RemoteMachine.Asked.Clear(); setup.Folders.Inspections = 0; setup.Git.Reads = 0; setup.Git.Refuse = true;
        var receipt = host.Sessions.FindByClient("accepted_owner", default, host.Runtime.Machine);
        Assert.NotNull(receipt.CheckoutOperationId);
        var other = setup.Hosts.Mac.Fixture.Db.Writer.Write("test.latest-project", scope => setup.Projects.AddOrRestore(scope,
            "workbox-test", setup.OtherRoot.FullName, "Latest project", null).Project);
        var used = setup.Projects.Get(setup.Project.Id)!.LastUsedAt;
        host.Fixture.Time.Advance(TimeSpan.FromMinutes(1));
        var count = host.Fixture.Count("engine_session"); var effects = host.Fixture.Count("engine_outbox");
        var revision = setup.Hosts.Mac.Fixture.Number("SELECT revision FROM projects_meta");
        var replay = Descriptor(setup, "accepted_owner") with
        {
            ProjectId = other.Id,
            CanonicalRoot = setup.OtherRoot.FullName,
            Create = new("no-longer-configured", "accepted_owner", other.Id,
                ExpectedSource: new(host.Runtime.Machine, 0, null))
        };
        var result = Result(await Call(socket, "create_at_root", replay));
        Assert.Equal(accepted, result.GetProperty("sessionId").GetString());
        Assert.Equal(receipt.CheckoutOperationId, result.GetProperty("checkoutOperationId").GetString());
        Assert.Equal(setup.Project.Id, result.GetProperty("projectId").GetString());
        var found = Result(await Call(socket, "byClient", new SessionsFeature.ByClientArgs("accepted_owner",
            ExpectedSource: new(host.Runtime.Machine, 0, null))));
        Assert.Equal(accepted, found.GetProperty("sessionId").GetString());
        Assert.Equal(setup.Project.Id, found.GetProperty("projectId").GetString());
        var unknown = Result(await Call(socket, "byClient", new SessionsFeature.ByClientArgs("never_accepted",
            ExpectedSource: new(host.Runtime.Machine, 0, null))));
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("sessionId").ValueKind);
        Assert.Equal(count, host.Fixture.Count("engine_session")); Assert.Equal(effects, host.Fixture.Count("engine_outbox"));
        Assert.Equal(revision, setup.Hosts.Mac.Fixture.Number("SELECT revision FROM projects_meta"));
        Assert.Equal(used, setup.Projects.Get(setup.Project.Id)!.LastUsedAt);
        Assert.Null(setup.Projects.Get(other.Id)!.LastUsedAt);
        Assert.Empty(setup.RemoteMachine.Asked); Assert.Equal(0, setup.Folders.Inspections);
        Assert.Equal(0, setup.Git.Reads); Assert.Equal(0, setup.Launcher.Launches);
    }

    private static SessionsFeature.CreateAtRootArgs Descriptor(Setup setup, string client) => new(
        setup.Hosts.Remote.Runtime.Machine, setup.Hosts.Mac.Runtime.Machine,
        new("claude", client, setup.Project.Id, ExpectedSource: new(setup.Hosts.Remote.Runtime.Machine, 0, null)),
        setup.Project.Id, setup.Root.FullName);
    private static ExpectedSource Source(TwoHosts hosts) => hosts.Mac.Fixture.Db.QueryFirst("""
        SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
        """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default,
        new SqlArg("$machine", hosts.Remote.Runtime.Machine))!;
    private static Task<ClientWebSocket> Open(TwoHosts.Host host) => ApiTests.OpenOwnerSocketAsync(host.Address, "isolated-token");
    private static Task<JsonElement> Call<T>(WebSocket socket, string method, T args) => Raw(socket, method, args switch
    {
        SessionsFeature.CreateAtRootArgs value => JsonSerializer.Serialize(value, SessionsJson.Default.CreateAtRootArgs),
        SessionsFeature.CreateArgs value => JsonSerializer.Serialize(value, SessionsJson.Default.CreateArgs),
        SessionsFeature.ByClientArgs value => JsonSerializer.Serialize(value, SessionsJson.Default.ByClientArgs),
        _ => throw new InvalidOperationException("Use the actual closed sessions DTO.")
    });
    private static Task<JsonElement> Raw(WebSocket socket, string method, string args) =>
        ApiTests.SocketRequestAsync(socket, 1, "sessions", method, args);
    private static JsonElement Result(JsonElement frame) => ApiTests.Result(frame);
    private static void Error(JsonElement frame, string code)
    { Assert.Equal("error", frame.GetProperty("t").GetString()); Assert.Equal(code, frame.GetProperty("code").GetString()); }

    private sealed class Setup : IAsyncDisposable
    {
        public readonly TwoHosts Hosts;
        private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("assembly-create-source-");
        public DirectoryInfo Root { get; }
        public DirectoryInfo OtherRoot { get; }
        public ProjectStore Projects { get; }
        public ProjectRecord Project { get; }
        public FakeMachine RemoteMachine { get; }
        public FakeMachine LocalMachine { get; }
        public FakeLauncher Launcher { get; } = new();
        public NoGit Git { get; } = new();
        public ObservedFolders Folders { get; }
        public int Forwards;
        public Setup()
        {
            Hosts = new(forwardCommands: true, machineLauncher: Launcher, checkouts: _home.FullName);
            Root = _home.CreateSubdirectory("original"); OtherRoot = _home.CreateSubdirectory("other");
            Projects = new(Hosts.Mac.Fixture.Db, Hosts.Mac.Fixture.Time);
            Project = Hosts.Mac.Fixture.Db.Writer.Write("test.project", scope => Projects.AddOrRestore(scope,
                "workbox-test", Root.FullName, "Original", null).Project);
            RemoteMachine = new(null, Hosts.Remote.Sessions); LocalMachine = new(null, Hosts.Mac.Sessions);
            Folders = new(new LocalProjectFolders(Git));
            Hosts.Remote.SessionFeature = host =>
            {
                var machines = new MachineSet(RemoteMachine);
                return new SessionsFeature(host.Sessions, machines,
                    new SessionRowSources(host.Fixture.Db, host.Sessions, machines),
                    owningMachine: host.Runtime.Machine, owningHome: _home.FullName, remoteFolders: Folders);
            };
            Hosts.Mac.SessionFeature = host =>
            {
                var machines = new MachineSet(LocalMachine);
                return new SessionsFeature(host.Sessions, machines,
                    new DeferredSources(() => new SessionRowSources(host.Fixture.Db, host.Sessions, machines, Hosts.Connections)),
                    owningMachine: host.Runtime.Machine, owningHome: _home.FullName,
                    remoteCreate: (_, _) => { Forwards++; throw new InvalidOperationException("Stale source reached forwarding."); },
                    remoteByClient: (_, _) => { Forwards++; throw new InvalidOperationException("Stale source reached forwarding."); });
            };
        }
        public Task Start() => Hosts.Start();
        public async ValueTask DisposeAsync() { await Hosts.DisposeAsync(); _home.Delete(true); }
    }

    private sealed class DeferredSources(Func<IRowSourceBindings> actual) : IRowSourceBindings
    {
        public Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct) =>
            actual().CaptureAsync(source, session, ct);
        public Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct) =>
            actual().CaptureEngineAsync(source, ct);
    }

    private sealed class ObservedFolders(IProjectFolders inner) : IProjectFolders
    {
        public int Inspections;
        public Task<FolderInspection> InspectAsync(string path, CancellationToken ct)
        { Interlocked.Increment(ref Inspections); return inner.InspectAsync(path, ct); }
        public Task<FolderListing> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<RefPage> RefsAsync(string root, string? checkout, string? filter, string? after, int limit, CancellationToken ct) =>
            inner.RefsAsync(root, checkout, filter, after, limit, ct);
        public Task<IReadOnlyList<Worktree>> WorktreesAsync(string root, CancellationToken ct) => inner.WorktreesAsync(root, ct);
    }
    private sealed class NoGit : IGit
    {
        public int Reads;
        public bool Refuse;
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdin, TimeSpan? limit, CancellationToken ct) =>
            throw new InvalidOperationException("No Git mutations in a create request.");
        public async IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Reads); ct.ThrowIfCancellationRequested();
            if (Refuse) throw new InvalidOperationException("Replay/refused ingress must not inspect Git.");
            // The owned folder is plain, with no Git repository. No external process is started.
            await Task.CompletedTask; yield break;
        }
    }
}
