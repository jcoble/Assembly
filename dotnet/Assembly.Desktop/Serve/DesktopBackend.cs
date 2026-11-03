using Assembly.Application.Features.Projects;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Infrastructure.Engine.Helpers;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Engine.Relays;
using CallFailed = Assembly.Application.Common.Calls.CallFailed;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine.Auth;
using Assembly.Infrastructure.Chat;
using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Titles;
using Assembly.Application.Features.Workflows;
using Assembly.Infrastructure.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Chat.Worktrees;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Topics;
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Clock;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Terminal;
using Assembly.Infrastructure.Updates;

namespace Assembly.Desktop.Serve;

/// <summary>
/// The desktop backend, running: the row store and sessions list in <c>home/assembly.db</c>, the live hub, the local
/// sessions and the API. Port 0 picks a free port (for tests).
/// </summary>
public sealed class DesktopBackend : IAsyncDisposable
{
    private readonly Func<Task> _beginStop;
    private readonly NeedAnswers _answers;
    private readonly EngineHelperCards _helperCards;
    private readonly MachineCalls _ownerMachineCalls;
    private readonly MachineRelay _relay;
    private readonly AppDatabase _db;
    private readonly BoardTools _board;
    private readonly CancellationTokenSource _hostStop;
    private readonly RemoteWorkflowMirror? _boardMirror;
    private readonly Topics _topics;
    private readonly Topics.Subscription? _boardMachines;
    private readonly LiveHub _hub;
    private readonly SessionRegistry _sessions;
    private readonly BackgroundWork _work;
    private readonly RemoteConnections _connections;
    private readonly WebApplication _app;
    private readonly FileStream _homeLock;
    private readonly TurnActivity _activity;
    private readonly MachinesFeature _machinesFeature;
    private readonly TerminalHost _terminals;
    private readonly HttpClient _cliHttp;

    private DesktopBackend(FileStream homeLock, AppDatabase db, LiveHub hub, SessionRegistry sessions, BackgroundWork work, RemoteConnections connections,
        WebApplication app, TurnActivity activity, MachinesFeature machinesFeature, TerminalHost terminals, BoardTools board, RemoteWorkflowMirror? boardMirror, Topics topics, Topics.Subscription? boardMachines, CancellationTokenSource hostStop, Func<Task> beginStop, NeedAnswers answers, EngineHelperCards helperCards, MachineCalls ownerMachineCalls, MachineRelay relay, HttpClient cliHttp)
    {
        _cliHttp = cliHttp;
        _beginStop = beginStop;
        _answers = answers;
        _helperCards = helperCards;
        _ownerMachineCalls = ownerMachineCalls;
        _relay = relay;
        _homeLock = homeLock;
        _terminals = terminals;
        _board = board;
        _hostStop = hostStop;
        _boardMirror = boardMirror;
        _topics = topics;
        _boardMachines = boardMachines;
        _machinesFeature = machinesFeature;
        _connections = connections;
        _db = db;
        _hub = hub;
        _sessions = sessions;
        _work = work;
        _app = app;
        _activity = activity;
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    /// <summary>Where the API listens, as <c>http://127.0.0.1:port</c>.</summary>
    public Uri Address { get; }

    /// <summary>Whether App Nap is held, while any turn runs.</summary>
    internal bool ActivityHeld => _activity.Held;

    /// <summary>How many of its background works are still running.</summary>
    internal int BackgroundRunning => _work.Running;

    /// <summary>
    /// Starts local serving independently of saved connection attempts. A supplied remote is a direct test endpoint;
    /// production startup imports SSH settings with savedRemote. Utility CLIs name new sessions.
    /// </summary>
    public static async Task<DesktopBackend> StartAsync(string home, string token, int port, IAgentLauncher launcher, TextWriter log, RemoteConfig? remote = null,
        RemoteTunnel? tunnel = null, (string Claude, string Codex)? utilityClis = null, SavedMachine? savedRemote = null,
        IEngineEffects? engineEffects = null, Action<EngineRuntime>? engineStarted = null)
    {
        Directory.CreateDirectory(home);
        // Before the database opens, so a second backend on this home writes nothing into the live one's; released if
        // the start fails, so the home isn't left locked.
        var homeLock = HomeLock.Take(home);
        try
        {
            return await StartAsync(homeLock, home, token, port, launcher, log, remote, tunnel, utilityClis, savedRemote, engineEffects, engineStarted);
        }
        catch
        {
            homeLock.Dispose();
            throw;
        }
    }

    private static async Task<DesktopBackend> StartAsync(FileStream homeLock, string home, string token, int port, IAgentLauncher launcher, TextWriter log,
        RemoteConfig? remote, RemoteTunnel? tunnel, (string Claude, string Codex)? utilityClis, SavedMachine? savedRemote,
        IEngineEffects? engineEffects, Action<EngineRuntime>? engineStarted)
    {
        var database = Path.Combine(home, "assembly.db");
        var topics = new Topics();
        var db = AppDatabase.Open(database, TimeProvider.System, topics);
        EngineRuntime? engine = null;
        WebApplication? app = null;
        SessionRegistry? sessions = null;
        WorkflowReceiptServices? workflowReceipts = null;
        BackgroundWork? work = null;
        LiveHub? hub = null;
        RemoteConnections? connections = null;
        MachinesFeature? machinesFeature = null;
        TerminalHost? terminals = null;
        BoardTools? board = null;
        RemoteWorkflowMirror? boardMirror = null;
        Topics.Subscription? boardMachines = null;
        CancellationTokenSource? hostStop = null;
        NeedAnswers? answers = null;
        EngineHelperCards? helperCards = null;
        MachineCalls? ownerMachineCalls = null;
        MachineRelay? relay = null;
        HttpClient? cliHttp = null;
        var activity = new TurnActivity();
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        var stopGate = new object();
        Task? stopped = null;
        Task BeginStop()
        {
            lock (stopGate)
            {
                if (stopped is not null) return stopped;
                var waits = answers?.StopWaiting() ?? Task.CompletedTask;
                var runtime = engine?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                endpoint.Stop();
                hostStop?.Cancel();
                return stopped = Task.WhenAll(waits, runtime);
            }
        }
        try
        {
            WorkflowSchema.Open(db);
            var preferences = new PreferenceStore(db);
            var sessionLimits = new SessionLimits(
                () => SessionLimitPreferences.Depth(preferences.Read("sessions.depthLimit")),
                () => SessionLimitPreferences.Children(preferences.Read("sessions.childLimit")));
            var boardQueries = new WorkflowQueries(db, remote?.Name);
            var boardTransitions = new WorkflowTransitions(db, TimeProvider.System);
            boardTransitions.Recover();
            // After the database opens, so a database the rename can't copy first stops with the checkouts left alone, and
            // before the API starts, so no session makes a checkout in sessions/ first.
            var checkouts = Path.Combine(home, "sessions");
            AdapterLauncher.MoveOldCheckouts(home, checkouts, log);
            var store = RowStore.Open(db);
            // Filled before recovery, whose closing rows go through the hub and so update it.
            var summaries = SessionSummaries.Load(store);
            var sessionStore = SessionStore.Open(db, TimeProvider.System, checkouts, summaries);
            _ = new ProjectStore(db, TimeProvider.System);
            // App Nap is held while any session has a run open, here or on a remote.
            hub = new LiveHub(store, topics: topics, summaries: summaries, summaryChanged: session =>
            {
                sessionStore.PublishSummary(session);
                activity.Hold(summaries.AnyRunOpen);
            });
            var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                runsHere: session => sessionStore.Get(session) is not { Machine: not null });
            if (closed > 0)
                log.WriteLine($"closed {closed} turn(s) the last run left unfinished");
            // Loading the summaries tells nobody: a remote's run left open holds it from the start.
            activity.Hold(summaries.AnyRunOpen);
            work = new BackgroundWork(TimeProvider.System, log);
            var attachments = new AttachmentStore(Path.Combine(home, "attachments"));
            var profiles = new MachineStore(db);
            var status = new MachineStatus(topics, []);

            var clock = new BackendClock(topics, TimeProvider.System, work);
            terminals = new TerminalHost(new UnixPtyStarter(), topics, TimeProvider.System, SessionTerminalFolders.ExistingFolder(checkouts, sessionStore));
            var probeFolder = Path.Combine(home, "probe");
            Directory.CreateDirectory(probeFolder);
            var challenges = new ChallengeBoard();
            bool IsConfiguredProvider(string provider, string account)
            {
                try
                {
                    var capture = ConfigTargetIdentity.Capture(launcher, provider);
                    var current = ConfigTargetIdentity.RequireSameConfiguration(launcher, capture.Descriptor, capture.Environment);
                    return AccountContext.Of(current, key => capture.Environment.GetValueOrDefault(key)) == account;
                }
                catch (CallFailed) { return false; }
            }
            hostStop = new CancellationTokenSource();
            helperCards = new EngineHelperCards(db, TimeProvider.System, log, sessionStore);
            var cutover = new SessionCutover(db, sessionStore, launcher, checkouts, TimeProvider.System, helperCards: helperCards);
            engine = EngineRuntime.Start(db, topics, TimeProvider.System, sessionLimits,
                cutover: cutover,
                effectsFactory: runtime =>
                {
                    sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, log, work,
                        titler: utilityClis is { } clis
                            ? new SessionTitler(sessionStore, store, new CliUtilityModel(clis.Claude, clis.Codex, () => preferences.Read("utilityModel"), TimeProvider.System, log))
                            : null,
                        feed: new EngineFeed(runtime, log, TimeProvider.System, helperCards),
                        mcp: McpSessionCallbacks.Create(runtime, endpoint, tokens));
                    sessions.BindHelperWorking(helperCards.Working, helperCards.Refresh);
                    workflowReceipts = WorkflowReceipts.Create(db, runtime.Machine, sessions);
                    if (engineEffects is not null) return engineEffects;
                    var catalogue = new AgentCatalogue(sessions, probeFolder, TimeProvider.System);
                    var runner = new SignInRunner(challenges, terminals, launcher, probeFolder, TimeProvider.System, log);
                    var signIn = new SignInEffects(db, runner, launcher, sessions, challenges, runtime, TimeProvider.System);
                    var proofRoot = Path.Combine(home, "workflow-proof");
                    var workspace = new WorkspaceEffects(db, runtime.Commands, runtime.Machine, proofRoot,
                        new WorkflowWorkspace(proofRoot), TimeProvider.System);
                    var commands = new CommandStepEffects(db, runtime.Commands, runtime.Machine, proofRoot, TimeProvider.System,
                        async (folder, ct) => (await new ReceiptCheckout().Capture(folder, ct)).Fingerprint);
                    return new EffectRouter(new AdapterEffects(db, sessions, sessionStore, store))
                        .Add("probe_provider", new ProviderEffects(db, catalogue, launcher, runtime, TimeProvider.System))
                        .Add("begin_sign_in", signIn)
                        .Add("cancel_sign_in", signIn)
                        .Add("workflow_setup", workspace)
                        .Add("workflow_workspace", workspace)
                        .Add("workflow_command", commands)
                        .Add("workflow_receipt", workflowReceipts.Effects);
                },
                readExpiryMinutes: MessageExpiryPreferences.ReadMinutes, effectsReady: endpoint.Ready,
                isConfiguredProvider: IsConfiguredProvider,
                workflowDispatchFactory: runtime => new WorkflowDispatchPreparation(db, runtime.Commands, runtime.Machine,
                    TimeProvider.System, Path.Combine(home, "workflow-proof")).PrepareAsync,
                effectCompleted: (scope, effectId) =>
                {
                    var childId = scope.QueryFirst("SELECT session_id FROM engine_outbox WHERE id=$id",
                        row => row.IsNull(0) ? null : row.Text(0), new SqlArg("$id", effectId));
                    if (childId is not null) helperCards.Observe(scope, childId);
                });
            var registry = sessions!;
            helperCards.Start(registry.Helpers.Row);
            var gate = new McpGate(engine, sessionLimits, TimeProvider.System);
            answers = new NeedAnswers(gate, engine, registry, TimeProvider.System);
            var machines = new MachineSet(new LocalMachine(hub, sessionStore, registry, attachments));
            machinesFeature = new MachinesFeature(machines, status, clock.Start, TimeProvider.System, store: profiles,
                connection: (name, connect, cancel) => connections!.ConnectionAsync(name, connect, cancel));
            engineStarted?.Invoke(engine);
            connections = new RemoteConnections(profiles, db.Writer, machines, status, hub, sessionStore, topics, log,
                machinesFeature, helperCards.Refresh,
                open: remote is null ? null : (profile, cancel) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    return Task.FromResult<(RemoteConfig, IAsyncDisposable?)>((remote, tunnel));
                }, mirrored: helperCards.Mirrored);
            connections.Start(savedRemote ?? (remote is null ? null : new SavedMachine(remote.Name, remote.Name)));
            // Direct test endpoints use the same owner; their callers historically expect routing ready on return.
            var startupName = savedRemote?.Name ?? remote?.Name;
            if (startupName is not null) await connections.ReadyAsync(startupName);
            var startup = connections.StartupSnapshot(startupName);
            board = new BoardTools(sessionStore, registry, boardTransitions, boardQueries, hostStop.Token);
            boardMirror = startup is { } connected ? new RemoteWorkflowMirror(startupName!, () => connections.StartupSnapshot(startupName)?.Config,
                boardQueries, boardTransitions, connected.Unreachable) : null;
            if (boardMirror is not null)
                boardMirror.AnswerDelivered += (sessionId, runId) =>
                    connections.StartupSnapshot(boardMirror.Name)?.Machine.WatchRun(sessionId, runId);
            var workflowCalls = new Assembly.Infrastructure.Workflows.WorkflowCalls(boardQueries, board.AnswerAsync, boardMirror);
            clock.Ticked += machinesFeature.Tick;
            boardMachines = boardMirror is null ? null : topics.Subscribe("machines", null, _ =>
            {
                foreach (var item in status.Items)
                    if (item.Name == boardMirror.Name && item.Reachable) boardMirror.Reconnect();
                clock.Start();
            }, (_, json) =>
            {
                // A removed machine is published as null; the mirror only follows its own machine's reachability.
                var item = JsonSerializer.Deserialize(json, SessionsJson.Default.MachineItem);
                if (item is not null && item.Name == boardMirror.Name && item.Reachable) boardMirror.Reconnect();
            });
            boardMirror?.Reconnect();
            var machineForward = new MachineForward(machine => connections.ByEngineMachine(db, machine));
            var starts = new MachineStarts(engine, machineForward, connections, sessionLimits, TimeProvider.System);
            var foreignStubs = new ForeignStubs(engine, connections, machineForward, TimeProvider.System);
            relay = new MachineRelay(engine, machineForward, connections, topics, TimeProvider.System, log, answers);
            ownerMachineCalls = new MachineCalls(engine, TimeProvider.System, launcher, checkouts, answers);
            var mcp = new McpServerHost(tokens, new McpRecorder(engine, gate, TimeProvider.System),
                [new StartSessionTool(gate, () => launcher.Agents.Select(agent => agent.Id).ToArray(), Directory.Exists, new McpMachineStarts(starts)),
                    new ListSessionsTool(gate), new SendMessageTool(gate, foreignStubs.EnsureAsync), new MessageStatusTool(gate),
                    new AskOwnerTool(gate, answers), new AnswerNeedTool(gate, answers), new ReportTool(gate, workflowReceipts!.Capture),
                    new WorkflowFormatTool(gate), new CheckWorkflowTool(gate, WorkflowLoader.Load), new StartWorkflowTool(gate, WorkflowLoader.Load),
                    new LinkTaskTool(gate), new GetSessionTitleTool(gate), new BoardUpdateTool(gate, board)]);
            var door = new EngineDoor(engine, sessionStore, machines, attachments, TimeProvider.System, answers);
            cliHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            var cliUpdates = new UpdateCalls(launcher, engine.Machine, cliHttp, TimeProvider.System, hostStop.Token,
                (machine, ct) => connections.CliLocator(db, machine, ct), new RemoteCliClient(cliHttp, TimeProvider.System));
            app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, mcp, db.Writer, topics,
                [cliUpdates, new EngineCalls(engine, TimeProvider.System, answers, machineForward, starts), new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(engine, machineForward), ownerMachineCalls, new SessionTitleCalls(engine, sessionStore, machineForward), new Assembly.Infrastructure.Engine.Sessions.SessionOpenCalls(db, engine, sessionStore, machines, connections), new ProvidersFeature(challenges, engine.Machine), new TerminalFeature(new RoutedTerminals(terminals, sessionStore, machines)), workflowCalls, new PreferencesFeature(db), clock, new RowsFeature(hub, store, sessionStore, machines, new Assembly.Infrastructure.Engine.Sessions.SessionRowSources(db, sessionStore, machines, connections)), new SessionsFeature(sessionStore, machines),
                    new SendFeature(sessionStore, machines, hub, door), new ConfigFeature(sessionStore, machines), machinesFeature,
                    new AttachmentsFeature(sessionStore, machines, Path.Combine(home, "staging", "attachments")),
                    new FilesFeature(sessionStore, new FileOwner(sessionStore, db)),
                    new Assembly.Application.Features.Workspaces.WorkspacesFeature(new Assembly.Application.Features.Workspaces.WorkspaceStore(db, TimeProvider.System)),
                    new WorktreesFeature(sessionStore),
                    new SourceControlFeature(sessionStore)], board, Engine: engine), port, out _);
            app.Lifetime.ApplicationStopping.Register(() => { _ = BeginStop(); });
            await app.StartAsync();
            var backend = new DesktopBackend(homeLock, db, hub, registry, work, connections, app, activity, machinesFeature, terminals, board, boardMirror, topics, boardMachines, hostStop, BeginStop, answers, helperCards, ownerMachineCalls, relay, cliHttp);
            endpoint.Listening(backend.Address);
            relay.Start();
            return backend;
        }
        catch
        {
            var stopping = BeginStop();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            if (answers is not null) await answers.DisposeAsync();
            if (relay is not null) await relay.DisposeAsync();
            if (ownerMachineCalls is not null) await ownerMachineCalls.DisposeAsync();
            await stopping;
            if (boardMachines is not null) topics.Unsubscribe(boardMachines);
            if (boardMirror is not null) await boardMirror.DisposeAsync();
            if (board is not null) await board.DisposeAsync();
            if (connections is not null) await connections.DisposeAsync();
            if (machinesFeature is not null) await machinesFeature.DisposeAsync();
            if (terminals is not null) await terminals.DisposeAsync();
            if (helperCards is not null) await helperCards.DisposeAsync();
            if (sessions is not null) await sessions.DisposeAsync();
            if (work is not null) await work.DisposeAsync();
            cliHttp?.Dispose();
            hub?.Dispose();
            activity.Hold(false);
            hostStop?.Dispose();
            db.Dispose();
            throw;
        }
    }

    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>
    /// Stops it, as SIGTERM does, once <paramref name="input"/> ends: the window's launcher holds this process's stdin
    /// open for its whole life, so its quit or crash ends this backend too. The dispose cancels the read.
    /// </summary>
    public void StopAtEndOf(Stream input) => _work.Start("stdin", async stop =>
    {
        var buffer = new byte[256];
        // WaitAsync, as a console stream's read doesn't see the token.
        while (await input.ReadAsync(buffer, stop).AsTask().WaitAsync(stop) > 0)
        {
        }
        _app.Lifetime.StopApplication();
    });

    public async ValueTask DisposeAsync()
    {
        var stopping = _beginStop();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _answers.DisposeAsync();
        await _relay.DisposeAsync();
        await _ownerMachineCalls.DisposeAsync();
        await stopping;
        if (_boardMachines is not null) _topics.Unsubscribe(_boardMachines);
        if (_boardMirror is not null) await _boardMirror.DisposeAsync();
        await _board.DisposeAsync();
        _hostStop.Dispose();
        // Before the remote, so no probe is still out on it.
        await _connections.DisposeAsync();
        await _machinesFeature.DisposeAsync();
        await _terminals.DisposeAsync();
        await _helperCards.DisposeAsync();
        await _sessions.DisposeAsync();
        // After the sessions, so a stop's cancel fails fast; before the database, which a write still finishing needs.
        await _work.DisposeAsync();
        _cliHttp.Dispose();
        _hub.Dispose();
        _activity.Hold(false);
        _db.Dispose();
        _homeLock.Dispose();
    }
}
