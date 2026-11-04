using Assembly.Engine.Contracts;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Chat.SourceControl;
using System.Net.WebSockets;
using System.Text;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Draft.Base.Chat;
using System.Text.Json;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Projects;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Projects;

namespace Assembly.Tests.Engine;

public sealed class FirstSendCheckoutTests : IDisposable
{
    private readonly EngineFixture _engine = new();
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("assembly-first-send-");
    private readonly SessionStore _sessions;
    private readonly ProjectStore _projects;
    private readonly Launcher _launcher = new();
    private readonly EngineCommands _commands;

    public FirstSendCheckoutTests()
    {
        _sessions = SessionStore.Open(_engine.Db, _engine.Time, "/unused-legacy-checkouts");
        _projects = new ProjectStore(_engine.Db, _engine.Time);
        var roots = new ProjectRoots(_projects);
        var cutover = new SessionCutover(_engine.Db, _sessions, _launcher, "/unused-legacy-checkouts", _engine.Time,
            projectRoots: roots, projectUse: roots, owningHome: _home.FullName);
        _engine.Db.Writer.Write("test.schema", scope => { cutover.EnsureSchema(scope); return 0; });
        _commands = new EngineCommands(_engine.Machine, _engine.Limits, projectStart: cutover.ProjectStart,
            projectProjection: _sessions);
        cutover.Attach(_commands, _engine.Machine);
    }

    [Fact]
    public void No_project_creates_shared_bare_folder_and_same_client_reconciles_without_another_start()
    {
        Assert.False(Directory.Exists(Path.Combine(_home.FullName, "assembly-bare")));
        var first = Create("stable_1", null, null);
        Assert.True(Directory.Exists(Path.Combine(_home.FullName, "assembly-bare")));
        var again = Create("stable_1", null, null);
        Assert.Equal(first.SessionId, again.SessionId);
        Assert.Equal(Path.Combine(_home.FullName, "assembly-bare"), _engine.Text("SELECT folder FROM engine_session WHERE id=$id", new SqlArg("$id", first.SessionId)));
        Assert.Null(_sessions.Get(first.SessionId)!.ProjectId);
        Assert.Equal(1, _engine.Count("engine_session"));
        Assert.Equal(1, _engine.Count("engine_outbox"));
        Assert.Equal(first.SessionId, _sessions.FindByClient("stable_1", default, _engine.Machine).SessionId);
        Assert.Equal("starting", _sessions.FindByClient("stable_1", default, _engine.Machine).State);
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Fact]
    public async Task Accepted_replay_through_the_owner_socket_does_not_require_the_agent_to_remain_configured()
    {
        await using var hosts = new TwoHosts();
        await hosts.Start();
        var owner = hosts.Mac;
        var cutover = new SessionCutover(owner.Fixture.Db, owner.Sessions, _launcher, "/unused-legacy-checkouts",
            owner.Fixture.Time, owningHome: _home.FullName);
        owner.Fixture.Db.Writer.Write("test.schema", scope => { cutover.EnsureSchema(scope); return 0; });
        cutover.Attach(owner.Runtime.Commands, owner.Runtime.Machine);
        var accepted = owner.Fixture.Db.Writer.Write("test.accepted", scope => owner.Sessions.Create(scope,
            "profile", "accepted_replay", Path.Combine(_home.FullName, "assembly-bare"), null, null));
        var starts = owner.Fixture.Count("engine_outbox");
        using var socket = await ApiTests.OpenOwnerSocketAsync(owner.Address, "isolated-token");
        // This provider is intentionally absent from the registered machine's catalogue.
        var replay = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, "sessions", "create",
            JsonSerializer.Serialize(new { agent = "no-longer-configured", clientId = "accepted_replay" })));
        Assert.Equal(accepted.SessionId, replay.GetProperty("sessionId").GetString());
        var facts = replay.GetProperty("facts");
        Assert.Equal(owner.Runtime.Machine, facts.GetProperty("machine").GetString());
        Assert.Equal("profile", facts.GetProperty("agent").GetString());
        Assert.Equal(Path.Combine(_home.FullName, "assembly-bare"), facts.GetProperty("folder").GetString());
        Assert.Equal(JsonValueKind.Null, facts.GetProperty("parentSessionId").ValueKind);
        Assert.Equal(1, owner.Fixture.Count("engine_session"));
        Assert.Equal(starts, owner.Fixture.Count("engine_outbox"));
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Fact]
    public async Task Registered_local_project_route_creates_on_the_actual_engine_without_rewriting_the_registry()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        var owner = hosts.Mac;
        var projects = new ProjectStore(owner.Fixture.Db, owner.Fixture.Time);
        var roots = new ProjectRoots(projects);
        var folders = new LocalProjectFolders(new GitCli());
        var machine = new FakeMachine(null, owner.Sessions);
        var cutover = new SessionCutover(owner.Fixture.Db, owner.Sessions, _launcher, "/unused-legacy-checkouts",
            owner.Fixture.Time, projectRoots: roots, projectUse: roots, owningHome: _home.FullName);
        owner.Fixture.Db.Writer.Write("test.schema", scope => { cutover.EnsureSchema(scope); return 0; });
        var commands = new EngineCommands(owner.Runtime.Machine, owner.Fixture.Limits,
            projectStart: cutover.ProjectStart, projectProjection: owner.Sessions);
        cutover.Attach(commands, owner.Runtime.Machine);
        owner.SessionFeature = host => new SessionsFeature(host.Sessions, new MachineSet(machine), projects: roots,
            owningMachine: host.Runtime.Machine, owningHome: _home.FullName);
        ICallRegistry? registered = null;
        owner.AdditionalFeatures = calls =>
        {
            registered = calls;
            new ProjectsFeature(projects, route => route == "local" ? folders : null, owner.Fixture.Db).Register(calls);
        };
        await hosts.Start();
        using var socket = await ApiTests.OpenOwnerSocketAsync(owner.Address, "isolated-token");
        var added = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, "projects", "add",
            JsonSerializer.Serialize(new { machine = "local", path = _home.FullName })));
        var project = added.GetProperty("project");
        var projectId = project.GetProperty("id").GetString()!;
        Assert.Equal("local", project.GetProperty("machine").GetString());
        Assert.NotEqual("local", owner.Runtime.Machine);
        var created = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 2, "sessions", "create",
            JsonSerializer.Serialize(new { agent = "claude", clientId = "registered_local", projectId })));
        var sessionId = created.GetProperty("sessionId").GetString()!;
        Assert.Equal(projectId, owner.Sessions.Get(sessionId)!.ProjectId);
        Assert.Equal(owner.Runtime.Machine, owner.Fixture.Text("SELECT machine FROM engine_session WHERE id=$id", new SqlArg("$id", sessionId)));
        Assert.Equal(_home.FullName, owner.Fixture.Text("SELECT folder FROM engine_session WHERE id=$id", new SqlArg("$id", sessionId)));
        Assert.Equal("pending", owner.Fixture.Text("SELECT checkout_state FROM engine_session WHERE id=$id", new SqlArg("$id", sessionId)));
        Assert.Equal("local", projects.Get(projectId)!.Machine);
        Assert.NotNull(projects.Get(projectId)!.LastUsedAt);
        Assert.Equal(0L, owner.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(0, _launcher.ProviderCalls);

        // Exercise the actual New Project producer, not a substitute projects/create endpoint.
        await hosts.StartMirror();
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var operations = new GitOperations(work, owner.Topics, TimeProvider.System, owner.Runtime.Machine);
        var git = new GitCli();
        var writes = new CheckoutWrites();
        var writer = new GitWriter(git);
        var coordinator = new NewProjectCoordinator(projects, owner.Fixture.Db, folders, writer, writes, operations, hosts.Connections);
        var gh = new GhCli();
        var prReader = new PullRequestReader(git, gh, _home.FullName, new GitTextPatch(git));
        var prs = new PullRequestWriter(git, gh, prReader);
        var utilityPreferences = new Assembly.Application.Features.Preferences.PreferenceStore(owner.Fixture.Db);
        var utility = new Assembly.Infrastructure.Agents.AcpUtilityModel([], () => utilityPreferences.Read("utilityModel"), TimeProvider.System, TextWriter.Null);
        var routes = new SourceControlFeature(owner.Sessions, owner.Fixture.Db, operations, owner.Forwarder, hosts.Connections, git);
        routes.Register(registered!);
        var sessionGit = new GitSessionOperations(owner.Sessions, git, prs, writes, operations, new(git, utility), prReader, owner.Fixture.Db);
        var projectPrReads = new ProjectPullRequestReads(owner.Fixture.Db, roots,
            new Assembly.Infrastructure.Engine.Sessions.SessionRowSources(owner.Fixture.Db, owner.Sessions, new MachineSet(machine), hosts.Connections),
            owner.Runtime.Machine, prReader, owner.Forwarder, hosts.Connections);
        new GitProjectOperations(operations, new(folders, writer, writes, operations), sessionGit, routes,
            new(projectPrReads, operations, sessionGit, owner.Forwarder), coordinator).Register(registered!);
        var host = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 3, "git", "operation_host",
            "{\"action\":\"createProject\"}"));
        var operation = Guid.NewGuid();
        var request = new GitOperationStart(host.GetProperty("instanceId").GetString()!, operation, null, "createProject",
            JsonSerializer.SerializeToElement(new NewProjectCreate("local", _home.FullName, "new-project", "git", null),
                ProjectsJson.Default.NewProjectCreate));
        ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 4, "git", "start",
            JsonSerializer.Serialize(request, GitOperationsJson.Default.GitOperationStart)));
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            t = "sub",
            id = 99,
            topic = "gitOperation",
            key = operations.Key(operation)
        })), WebSocketMessageType.Text, true, default);
        JsonElement outcome;
        while (true)
        {
            var frame = await ApiTests.ReadFrameAsync(socket);
            outcome = frame.GetProperty("t").GetString() == "state"
                ? frame.GetProperty("items")[0].GetProperty("item") : frame.GetProperty("item");
            if (outcome.GetProperty("state").GetString() is "succeeded" or "failed" or "uncertain" or "cancelled") break;
        }
        Assert.Equal("succeeded", outcome.GetProperty("state").GetString());
        var newProject = outcome.GetProperty("result").GetProperty("project");
        Assert.Equal("local", newProject.GetProperty("machine").GetString());
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"unsub\",\"id\":99}"), WebSocketMessageType.Text, true, default);
        var newId = newProject.GetProperty("id").GetString()!;
        var newSession = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 5, "sessions", "create",
            JsonSerializer.Serialize(new { agent = "claude", clientId = "new_project_first_send", projectId = newId })));
        var newSessionId = newSession.GetProperty("sessionId").GetString()!;
        Assert.Equal(Path.Combine(_home.FullName, "new-project"), owner.Sessions.Root(owner.Sessions.Get(newSessionId)!));
        Assert.Equal(newId, owner.Sessions.Get(newSessionId)!.ProjectId);
        Assert.Equal("pending", owner.Fixture.Text("SELECT checkout_state FROM engine_session WHERE id=$id", new SqlArg("$id", newSessionId)));
        Assert.Equal(0L, owner.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Fact]
    public async Task Project_checkout_is_pending_without_start_and_creation_touch_roll_back_together()
    {
        var project = _engine.Db.Writer.Write("test.project", s => _projects.AddOrRestore(s, "local", _home.FullName, "Project", null).Project);
        var created = Create("project_1", project.Id, new("current", null, null));
        Assert.Equal(project.Id, _sessions.Get(created.SessionId)!.ProjectId);
        Assert.Equal("pending", _engine.Text("SELECT checkout_state FROM engine_session WHERE id=$id", new SqlArg("$id", created.SessionId)));
        Assert.Equal(0L, _engine.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        var location = _sessions.Location(created);
        Assert.NotNull(location);
        Assert.Equal(_home.FullName, location.Folder);
        Assert.False(Directory.Exists(Path.Combine(_home.FullName, "assembly-bare")));
        Assert.False(location.Ready);
        var opener = new SessionOpener(_sessions, _launcher, _ => throw new InvalidOperationException("No adapter before checkout."));
        var notReady = await Assert.ThrowsAsync<CallFailed>(() => opener.OpenAsync(created.SessionId, "First prompt",
            new StageTimes(_engine.Time), default));
        Assert.Equal("checkout_not_ready", notReady.Reason);
        Assert.NotNull(_projects.Get(project.Id)!.LastUsedAt);
        var used = _projects.Get(project.Id)!.LastUsedAt;
        _engine.Time.Advance(TimeSpan.FromMinutes(1));
        var sessions = _engine.Count("engine_session");
        var effects = _engine.Count("engine_outbox");
        Assert.Throws<IOException>(() => _engine.Db.Writer.Write("test.rollback", s =>
        {
            _sessions.Create(s, "profile", "rollback_1", _home.FullName, project.Id, new("current", null, null));
            throw new IOException("rollback all");
#pragma warning disable CS0162
            return 0;
#pragma warning restore CS0162
        }));
        Assert.Equal(sessions, _engine.Count("engine_session"));
        Assert.Equal(effects, _engine.Count("engine_outbox"));
        Assert.Null(_sessions.FindByClient("rollback_1", default, _engine.Machine).SessionId);
        Assert.Equal(used, _projects.Get(project.Id)!.LastUsedAt);
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Fact]
    public void Correlation_never_relabels_a_later_operation_or_uses_an_uncertified_current_operation()
    {
        var project = _engine.Db.Writer.Write("test.project", s =>
            _projects.AddOrRestore(s, "local", _home.FullName, "Project", null).Project);
        var session = Create("operation_1", project.Id, new("current", null, null));
        var initial = _sessions.FindByClient("operation_1", default, _engine.Machine);
        Assert.NotNull(initial.CheckoutOperationId);
        Assert.Equal("pending", initial.CheckoutState);
        Assert.Equal(initial.CheckoutOperationId, _engine.Text("SELECT checkout_operation_id FROM engine_session WHERE id=$id",
            new SqlArg("$id", session.SessionId)));
        // Explicit negative persisted facts exercise stale/corrupt projection rejection, not a positive checkout witness.
        _engine.Db.Writer.Write("test.later-operation", s => s.Execute("""
            UPDATE engine_session SET checkout_operation_id=$later,checkout_state='ready' WHERE id=$id
            """, new("$later", Guid.NewGuid().ToString("D")), new("$id", session.SessionId)));
        var later = _sessions.FindByClient("operation_1", default, _engine.Machine);
        Assert.Equal(initial.CheckoutOperationId, later.CheckoutOperationId);
        Assert.Null(later.CheckoutState);
        _engine.Db.Writer.Write("test.missing-carrier", s => s.Execute("""
            UPDATE engine_session SET checkout_intent_json='{}' WHERE id=$id
            """, new SqlArg("$id", session.SessionId)));
        var missing = _sessions.FindByClient("operation_1", default, _engine.Machine);
        Assert.Null(missing.CheckoutOperationId);
        Assert.Null(missing.CheckoutState);
    }

    [Fact]
    public void Child_inherits_actual_parent_project_and_a_forged_project_rolls_back()
    {
        var project = _engine.Db.Writer.Write("test.project", s =>
            _projects.AddOrRestore(s, "local", _home.FullName, "Project", null).Project);
        var inheritedFolder = Directory.CreateDirectory(Path.Combine(_home.FullName, "existing-parent-folder")).FullName;
        const string parentId = "existing-parent";
        _engine.SeedSession(parentId, state: "idle", provider: "profile");
        _engine.Db.Writer.Write("test.existing-parent", scope =>
        {
            // Existing owned Engine facts; this test certifies folder association, not Git/native history.
            scope.Execute("UPDATE engine_session SET folder=$folder WHERE id=$id",
                new("$folder", inheritedFolder), new("$id", parentId));
            _sessions.ProjectEngineSession(scope, new(parentId, "profile", inheritedFolder, "Parent", null, 1000), null, inheritedFolder);
            Assert.True(_commands.Apply(scope, new(Guid.NewGuid(), _engine.Machine, new(ActorKind.System, "projects"),
                parentId, 0, 0, 1000), new AssignProject(project.Id)).Applied);
            return 0;
        });
        var child = _engine.Db.Writer.Write("test.child", s => _sessions.Create(s, "profile", "child_1", inheritedFolder,
            null, null, parentId, "Child", parentId));
        Assert.Equal(project.Id, _sessions.Get(child.SessionId)!.ProjectId);
        Assert.Equal(inheritedFolder, _engine.Text("SELECT folder FROM engine_session WHERE id=$id", new SqlArg("$id", child.SessionId)));
        Assert.NotEqual(project.RootPath, inheritedFolder);
        Assert.Equal(project.Id, _engine.Text("SELECT project_id FROM engine_session WHERE id=$id", new SqlArg("$id", child.SessionId)));
        Assert.Throws<CallBadArguments>(() => _engine.Db.Writer.Write("test.forged", s => _sessions.Create(s,
            "profile", "forged_1", inheritedFolder, "other-project", null, parentId, "Child", parentId)));
        Assert.Null(_sessions.FindByClient("forged_1", default, _engine.Machine).SessionId);
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("removed")]
    [InlineData("wrong_owner")]
    public void Unavailable_project_never_falls_back_to_home_or_commits_a_session(string state)
    {
        var projectId = "missing-project";
        if (state != "missing")
            projectId = _engine.Db.Writer.Write("test.project", scope =>
            {
                var project = _projects.AddOrRestore(scope, state == "wrong_owner" ? "other-route" : "local",
                    _home.FullName, "Project", null).Project;
                if (state == "removed") _projects.Remove(scope, project.Id, false);
                return project.Id;
            });
        Assert.Throws<CallFailed>(() => Create("refused_1", projectId, new("current", null, null)));
        Assert.Equal(0, _engine.Count("engine_session"));
        Assert.Equal(0, _engine.Count("engine_outbox"));
        Assert.Empty(_sessions.List(null, 20).Sessions);
        Assert.Null(_sessions.FindByClient("refused_1", default, _engine.Machine).SessionId);
        Assert.Equal(0, _launcher.ProviderCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData("a/b")]
    public void Invalid_client_id_commits_nothing(string client)
    {
        Assert.Throws<CallBadArguments>(() => Create(client, null, null));
        Assert.Equal(0, _engine.Count("engine_session"));
        Assert.Empty(_sessions.List(null, 20).Sessions);
    }

    private SessionInfo Create(string client, string? project, CheckoutIntent? checkout) =>
        _engine.Db.Writer.Write("test.create", s => _sessions.Create(s, "profile", client, project is null ? Path.Combine(_home.FullName, "assembly-bare") : _home.FullName, project, checkout));

    public void Dispose() { _engine.Dispose(); _home.Delete(true); }

    private sealed class Launcher : IAgentLauncher
    {
        public int ProviderCalls;
        public IReadOnlyList<AgentDescriptor> Agents { get; } = [new("profile", "Profile", "unused", [], null, VendorFamily: "codex")];
        public bool CanRunTerminalAuthentication => false;
        public void ValidateFolder(string folder) { if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder); }
        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
        { ProviderCalls++; throw new InvalidOperationException("No provider for creation."); }
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            StageTimes stages, CancellationToken cancellationToken) => LaunchAsync(descriptor.Id, stages, cancellationToken);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) => LaunchAsync(descriptor.Id, stages, cancellationToken);
    }
}
