using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class SessionAcceptedAncestryTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Bare_create_forwarded_to_remote_uses_its_own_home_and_keeps_project_null(string agent)
    {
        await using var f = new Fixture(separateHomes: true);
        var remoteHome = f.RemoteHome;
        var owner = f.Hosts.Remote;
        await f.Start();
        using var socket = await f.OpenDesktop();
        var created = Result(await ApiTests.SocketRequestAsync(socket, 1, "sessions", "create",
            JsonSerializer.Serialize(new SessionsFeature.CreateArgs(agent, "bare-remote", Machine: "workbox-test",
                ExpectedSource: f.Source()), SessionsJson.Default.CreateArgs)));
        var id = created.GetProperty("sessionId").GetString()!;
        var folder = Path.Combine(remoteHome, "assembly-bare");
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.False(Directory.Exists(Path.Combine(f.Home.FullName, "assembly-bare")));
        Assert.Equal(folder, created.GetProperty("facts").GetProperty("folder").GetString());
        Assert.Equal(agent, created.GetProperty("facts").GetProperty("agent").GetString());
        Assert.Equal(owner.Runtime.Machine, created.GetProperty("facts").GetProperty("machine").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("projectId").ValueKind);
        Assert.Null(owner.Sessions.Get(id)!.ProjectId);
        Assert.Equal(folder, owner.Sessions.Root(owner.Sessions.Get(id)!));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Accepted_root_and_helper_replay_keep_actual_owner_provider_and_certified_parent_without_provider_launch()
    {
        await using var f = new Fixture(); await f.Start();
        using var socket = await f.OpenDesktop();
        var root = Result(await f.Create(socket, "accepted-root"));
        var rootId = root.GetProperty("sessionId").GetString()!;
        await f.CompleteInitialStart(rootId);
        var child = Result(await f.Create(socket, "accepted-child", rootId));
        var childId = child.GetProperty("sessionId").GetString()!;
        var chain = f.Known(childId);
        Assert.Equal([rootId, childId], chain.Select(n => n.SessionId));
        Assert.All(chain, node => { Assert.Equal(f.Hosts.Remote.Runtime.Machine, node.Machine); Assert.Equal("claude", node.Provider); });
        Assert.Null(chain[0].ParentId); Assert.Equal(rootId, chain[1].ParentId);
        Assert.Equal(rootId, f.Hosts.Mac.Sessions.Get(childId)!.ParentSessionId);
        Assert.Equal("workbox-test", f.Hosts.Mac.Sessions.Get(childId)!.Machine);
        var receipts = f.Hosts.Mac.Fixture.Count("engine_command_receipt");
        var starts = f.Hosts.Remote.Fixture.Count("engine_outbox");
        Assert.Equal(childId, Result(await f.Create(socket, "accepted-child", rootId)).GetProperty("sessionId").GetString());
        Assert.Equal(childId, Result(await f.ByClient(socket, "accepted-child")).GetProperty("sessionId").GetString());
        Assert.Equal(chain, f.Known(childId));
        Assert.Equal(receipts, f.Hosts.Mac.Fixture.Count("engine_command_receipt"));
        Assert.Equal(starts, f.Hosts.Remote.Fixture.Count("engine_outbox"));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_actual_local_owner_or_conflicting_foreign_provider_cannot_be_blessed_by_accepted_replay(bool foreignProvider)
    {
        await using var f = new Fixture(); await f.Start();
        using var owner = await f.OpenOwner();
        var accepted = Result(await f.CreateOwner(owner, "collision"));
        var id = accepted.GetProperty("sessionId").GetString()!;
        using var desktop = await f.OpenDesktop();
        if (foreignProvider)
        {
            var command = new MachineCommand(Guid.NewGuid(), f.Hosts.Remote.Runtime.Machine, id, 0, 0,
                new RegisterForeignSession(f.Hosts.Remote.Runtime.Machine, "codex", "An uncertified reference"));
            var result = Result(await ApiTests.SocketRequestAsync(desktop, 2, "engine", "machine_command",
                JsonSerializer.Serialize(command, EngineSocketJson.Default.MachineCommand)));
            Assert.True(result.GetProperty("applied").GetBoolean());
        }
        else
        {
            var command = new WindowCommand(Guid.NewGuid(), id, 0, 0,
                new StartSession("claude", null, f.Home.FullName, "Actual local owner", null, null, null));
            var result = Result(await ApiTests.SocketRequestAsync(desktop, 2, "engine", "command",
                JsonSerializer.Serialize(command, EngineSocketJson.Default.WindowCommand)));
            Assert.True(result.GetProperty("applied").GetBoolean());
        }
        var before = f.Hosts.Mac.Fixture.Count("engine_command_receipt");
        var beforeFacade = f.Hosts.Mac.Sessions.Get(id);
        if (foreignProvider) Assert.Null(beforeFacade);
        else { Assert.NotNull(beforeFacade); Assert.Null(beforeFacade.Machine); }
        var refused = await f.ByClient(desktop, "collision");
        Assert.Equal("error", refused.GetProperty("t").GetString());
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal(beforeFacade, f.Hosts.Mac.Sessions.Get(id));
        Assert.Equal(before, f.Hosts.Mac.Fixture.Count("engine_command_receipt"));
        if (foreignProvider) Assert.Null(f.TryKnown(id));
        else Assert.Equal(f.Hosts.Mac.Runtime.Machine, Assert.Single(f.Known(id)).Machine);
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Cancellation_after_prepared_ancestry_apply_rolls_back_reference_receipts_facade_and_publication_then_byClient_reconciles()
    {
        await using var f = new Fixture(); await f.Start();
        var source = f.Source();
        var captured = await f.Hosts.Connections.CaptureSessionOpenSourceAsync(f.Hosts.Mac.Fixture.Db, source, default);
        var owner = captured.Live.OwnerBinding;
        var read = await captured.Machine.CaptureOwnerReadAsync(source.Machine, default);
        var before = f.Hosts.Mac.Fixture.Count("engine_command_receipt");
        var sessions = f.Hosts.Mac.Fixture.Count("engine_session");
        var facades = f.Hosts.Mac.Fixture.Count("session_info");
        var publications = 0;
        var subscription = f.Hosts.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, _) => publications++);
        using var stop = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captured.Machine.CreateBoundSessionAsync(
                new("claude", "cancelled-acceptance", ExpectedSource: source), null, owner, read.Socket, captured.Live, stop.Token,
                (scope, result) =>
                {
                    Assert.NotNull(f.Hosts.Mac.Runtime.Commands.ReadKnownAncestry(scope, result.SessionId));
                    stop.Cancel(); stop.Token.ThrowIfCancellationRequested();
                }));
            Assert.Equal(before, f.Hosts.Mac.Fixture.Count("engine_command_receipt"));
            Assert.Equal(sessions, f.Hosts.Mac.Fixture.Count("engine_session"));
            Assert.Equal(facades, f.Hosts.Mac.Fixture.Count("session_info"));
            Assert.Equal(0, publications);
        }
        finally { f.Hosts.Mac.Topics.Unsubscribe(subscription); }
        using var socket = await f.OpenDesktop();
        var known = Result(await f.ByClient(socket, "cancelled-acceptance"));
        var id = known.GetProperty("sessionId").GetString()!;
        Assert.Single(f.Known(id)); Assert.NotNull(f.Hosts.Mac.Sessions.Get(id));
        using var remote = await f.OpenOwner();
        Assert.Equal(id, Result(await ApiTests.SocketRequestAsync(remote, 1, "sessions", "byClient",
            """{"clientId":"cancelled-acceptance"}""")).GetProperty("sessionId").GetString());
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task A_replaced_source_cannot_reconcile_an_old_selection_or_create_a_second_accepted_session()
    {
        await using var f = new Fixture(); await f.Start();
        using var socket = await f.OpenDesktop();
        var old = f.Source();
        var accepted = Result(await f.Create(socket, "replace-source"));
        var id = accepted.GetProperty("sessionId").GetString()!;
        var before = f.Hosts.Remote.Fixture.Count("engine_outbox");
        var captured = await f.Hosts.Connections.CaptureSessionOpenSourceAsync(f.Hosts.Mac.Fixture.Db, old, default);
        await f.Hosts.DisconnectMirror();
        Assert.True(captured.Live.IsInvalidated);
        await f.Hosts.ReconnectSaved();
        var resumed = await f.ReadySource();
        Assert.Equal(old.Machine, resumed.Machine); Assert.Equal(old.RouteMachine, resumed.RouteMachine);
        Assert.True(resumed.Generation > 0);
        Assert.Equal(id, Result(await f.ByClient(socket, "replace-source")).GetProperty("sessionId").GetString());
        Assert.Equal(before, f.Hosts.Remote.Fixture.Count("engine_outbox"));
        var receipts = f.Hosts.Mac.Fixture.Count("engine_command_receipt");
        await f.Hosts.DisconnectMirror();
        var replacement = new TwoHosts.Host(windowCalls: true, machineLauncher: f.Launcher,
            checkouts: Path.Combine(f.Home.FullName, "replacement"), owningHome: f.Home.FullName);
        replacement.SessionSources = new SessionRowSources(replacement.Fixture.Db, replacement.Sessions,
            new MachineSet(new FakeMachine(null, replacement.Sessions)));
        await f.Hosts.ReplaceAtSameEndpoint(replacement);
        await f.Hosts.ReconnectSaved(); await f.Hosts.Reached(f.Hosts.Remote.Through);
        var current = await f.ReadySource(); Assert.NotEqual(old.Machine, current.Machine);
        Assert.Equal(old.RouteMachine, current.RouteMachine);
        var stale = await ApiTests.SocketRequestAsync(socket, 1, "sessions", "byClient",
            JsonSerializer.Serialize(new SessionsFeature.ByClientArgs("replace-source", "workbox-test", old), SessionsJson.Default.ByClientArgs));
        Assert.Equal("error", stale.GetProperty("t").GetString()); Assert.Equal("source_changed", stale.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, Result(await f.ByClient(socket, "replace-source")).GetProperty("sessionId").ValueKind);
        Assert.Equal(old.Machine, Assert.Single(f.Known(id)).Machine);
        Assert.Equal(receipts, f.Hosts.Mac.Fixture.Count("engine_command_receipt"));
        Assert.Equal(0, f.Hosts.Remote.Fixture.Count("engine_outbox"));
        Assert.Equal(0, f.Launcher.Launches);
    }

    [Fact]
    public async Task Disconnection_during_actual_owner_check_cannot_publish_prepared_reference_or_facade()
    {
        await using var f = new Fixture(); await f.Start();
        using var owner = await f.OpenOwner();
        var created = Result(await f.CreateOwner(owner, "inflight-replacement"));
        var id = created.GetProperty("sessionId").GetString()!;
        var checkedOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hosts.Remote.MachineReading = async (method, call) =>
        {
            if (method != "foreign_title_source_check") return;
            checkedOwner.TrySetResult(); await release.Task.WaitAsync(call.Cancellation);
        };
        var receipts = f.Hosts.Mac.Fixture.Count("engine_command_receipt");
        using var desktop = await f.OpenDesktop();
        var pending = f.ByClient(desktop, "inflight-replacement");
        try
        {
            await checkedOwner.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Hosts.DisconnectMirror().WaitAsync(TimeSpan.FromSeconds(10));
            var frame = await pending;
            Assert.Equal("error", frame.GetProperty("t").GetString());
            Assert.Equal("source_changed", frame.GetProperty("code").GetString());
            Assert.Null(f.Hosts.Mac.Sessions.Get(id)); Assert.Null(f.TryKnown(id));
            Assert.Equal(receipts, f.Hosts.Mac.Fixture.Count("engine_command_receipt"));
            Assert.Equal(0, f.Launcher.Launches);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Missing_local_engine_refuses_new_acceptance_before_owner_create_and_leaves_legacy_copy_uncertified()
    {
        await using var f = new Fixture(); await f.Start();
        using var hub = new LiveHub(RowStore.Open(f.Hosts.Mac.Fixture.Db));
        await using var machine = new global::Assembly.Infrastructure.Remote.RemoteMachine("workbox-test", f.Hosts.Remote.Address,
            "isolated-token", hub, f.Hosts.Mac.Sessions, TextWriter.Null,
            new MachineStatus(f.Hosts.Mac.Topics, []));
        await machine.ProbeAsync(default);
        var identity = await machine.ReadMirrorOwnerAsync(default);
        var read = await machine.CaptureOwnerReadAsync(identity.Info.Machine, default);
        var count = f.Hosts.Remote.Fixture.Count("engine_session");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => machine.CreateBoundSessionAsync(new("claude", "no-engine"),
            null, identity.Binding, read.Socket, null, default));
        Assert.Equal("configuration_unavailable", failure.Reason);
        Assert.Equal(count, f.Hosts.Remote.Fixture.Count("engine_session"));
        Assert.Null(f.Hosts.Remote.Sessions.FindByClient("no-engine", default, f.Hosts.Remote.Runtime.Machine).SessionId);
        f.Hosts.Mac.Sessions.Add("legacy-copy", "claude", "", "workbox-test");
        Assert.Null(f.TryKnown("legacy-copy"));
        Assert.Equal(0, f.Launcher.Launches);
    }

    private static JsonElement Result(JsonElement frame)
    {
        var type = frame.GetProperty("t").GetString();
        var code = frame.TryGetProperty("code", out var c) ? c.GetString() : null;
        var message = frame.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(type == "result", $"Expected result; t={type}, code={code}, message={message}");
        return ApiTests.Result(frame);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal DirectoryInfo Home { get; } = Directory.CreateTempSubdirectory("assembly-accepted-ancestry-");
        internal FakeLauncher Launcher { get; } = new();
        internal TwoHosts Hosts { get; }
        internal string RemoteHome { get; }
        internal Fixture(bool separateHomes = false)
        {
            RemoteHome = separateHomes ? Directory.CreateDirectory(Path.Combine(Home.FullName, "remote-home")).FullName : Home.FullName;
            Hosts = new(windowCalls: true, forwardCommands: true, machineLauncher: Launcher, checkouts: Home.FullName,
                owningHome: Home.FullName, remoteOwningHome: RemoteHome);
            var projects = new ProjectStore(Hosts.Mac.Fixture.Db, Hosts.Mac.Fixture.Time);
            Hosts.Remote.SessionFeature = host =>
            {
                var machines = new MachineSet(new FakeMachine(null, host.Sessions)
                {
                    Agents = [new("claude", "Claude", new string('a', 64), "", "claude/"),
                        new("codex", "Codex", new string('b', 64), "", "codex/")]
                });
                return new SessionsFeature(host.Sessions, machines, new SessionRowSources(host.Fixture.Db, host.Sessions, machines),
                    owningMachine: host.Runtime.Machine, owningHome: RemoteHome);
            };
            Hosts.Mac.SessionFeature = host =>
            {
                var machines = new MachineSet(new FakeMachine(null, host.Sessions));
                return new SessionsFeature(host.Sessions, machines,
                    new DeferredSources(() => new SessionRowSources(host.Fixture.Db, host.Sessions, machines, Hosts.Connections)),
                    owningMachine: host.Runtime.Machine, owningHome: Home.FullName,
                    remoteCreate: (args, call) => Hosts.Connections.CreateSessionAsync(host.Fixture.Db, projects, host.Runtime.Machine, args, call),
                    remoteByClient: (args, call) => Hosts.Connections.ReadCreatedSessionAsync(host.Fixture.Db, projects, args, call));
            };
        }
        internal async Task Start() { await Hosts.Start(); await Hosts.StartMirror(); }
        internal async Task CompleteInitialStart(string session)
        {
            var host = Hosts.Remote;
            var engine = (IEngineRuntime)host.Runtime;
            var effectId = engine.Write(scope =>
            {
                var pending = scope.QueryFirst("SELECT id,version FROM engine_outbox WHERE session_id=$session AND kind='start' AND state='pending'",
                    row => (Id: row.Text(0), Version: row.Long(1)), new SqlArg("$session", session));
                Assert.NotNull(pending.Id);
                var claimed = host.Runtime.Commands.Apply(scope, new(Guid.NewGuid(), host.Runtime.Machine,
                    new(ActorKind.System, "test.start-effect"), pending.Id, pending.Version, 0, host.Fixture.Time.GetUtcNow().ToUnixTimeMilliseconds()), new ClaimEffect());
                Assert.True(claimed.Applied, claimed.Error);
                return pending.Id;
            });
            var effect = EffectTransitions.Read(host.Fixture.Db, effectId, default)!;
            var actual = new AdapterEffects(host.Fixture.Db, host.NeedHosts, host.Sessions, RowStore.Open(host.Fixture.Db));
            var receipt = await actual.ExecuteAsync(effect, default);
            Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
            engine.Write(scope =>
            {
                var version = scope.QueryFirst("SELECT version FROM engine_outbox WHERE id=$id", row => row.Long(0), new SqlArg("$id", effectId));
                var completed = host.Runtime.Commands.Apply(scope, new(Guid.NewGuid(), host.Runtime.Machine,
                    new(ActorKind.System, "test.start-effect"), effectId, version, 0, host.Fixture.Time.GetUtcNow().ToUnixTimeMilliseconds()), new CompleteEffect(receipt));
                Assert.True(completed.Applied, completed.Error);
                Assert.True(host.Sessions.OwningLocation(scope, session)!.Ready);
                Assert.Equal("idle", scope.QueryFirst("SELECT state FROM engine_session WHERE id=$id", row => row.Text(0), new SqlArg("$id", session)));
                return 0;
            });
        }

        internal Task<ClientWebSocket> OpenDesktop() => ApiTests.OpenOwnerSocketAsync(Hosts.Mac.Address, "isolated-token");
        internal Task<ClientWebSocket> OpenOwner() => ApiTests.OpenOwnerSocketAsync(Hosts.Remote.Address, "isolated-token");
        internal async Task<ExpectedSource> ReadySource()
        {
            var owned = Hosts.Connections.CaptureMirrorOwners().Single();
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
                var source = Source();
                Assert.Equal(live.Machine, source.Machine); Assert.Equal(live.Generation, source.Generation);
                var current = await Hosts.Connections.CaptureSessionOpenSourceAsync(Hosts.Mac.Fixture.Db, source, default);
                Assert.Same(owned.Machine, current.Machine);
                Assert.Equal(source.Machine, live.OwnerBinding.Machine);
                Assert.Same(live, current.Live); current.EnsureCurrent();
                return source;
            }
            finally { mirror.CaughtUp -= Ready; }
        }
        internal ExpectedSource Source() => Hosts.Mac.Fixture.Db.QueryFirst("""
            SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
            """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default,
            new SqlArg("$machine", Hosts.Remote.Runtime.Machine))!;
        internal Task<JsonElement> Create(WebSocket socket, string client, string? parent = null) =>
            ApiTests.SocketRequestAsync(socket, 1, "sessions", "create", JsonSerializer.Serialize(new SessionsFeature.CreateArgs(
                "claude", client, Machine: "workbox-test", ParentSessionId: parent, Title: "Accepted", ExpectedSource: Source()), SessionsJson.Default.CreateArgs));
        internal Task<JsonElement> CreateOwner(WebSocket socket, string client) => ApiTests.SocketRequestAsync(socket, 1, "sessions", "create",
            JsonSerializer.Serialize(new SessionsFeature.CreateArgs("claude", client, Title: "Accepted"), SessionsJson.Default.CreateArgs));
        internal Task<JsonElement> ByClient(WebSocket socket, string client) => ApiTests.SocketRequestAsync(socket, 1, "sessions", "byClient",
            JsonSerializer.Serialize(new SessionsFeature.ByClientArgs(client, "workbox-test", Source()), SessionsJson.Default.ByClientArgs));
        internal IReadOnlyList<ForeignAncestor>? TryKnown(string id)
        { using var scope = Hosts.Mac.Fixture.Db.OpenSnapshot(default); return Hosts.Mac.Runtime.Commands.ReadKnownAncestry(scope, id); }
        internal IReadOnlyList<ForeignAncestor> Known(string id) => Assert.IsAssignableFrom<IReadOnlyList<ForeignAncestor>>(TryKnown(id));
        public async ValueTask DisposeAsync() { await Hosts.DisposeAsync(); Home.Delete(true); }
    }

    private sealed class DeferredSources(Func<IRowSourceBindings> actual) : IRowSourceBindings
    {
        public Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct) => actual().CaptureAsync(source, session, ct);
        public Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct) => actual().CaptureEngineAsync(source, ct);
    }
}
