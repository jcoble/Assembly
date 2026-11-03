using Assembly.Application.Features.Projects;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine.Workflows;
using Microsoft.AspNetCore.Builder;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Infrastructure.Engine.Helpers;
using Assembly.Infrastructure.Engine.Needs;
using CallFailed = Assembly.Application.Common.Calls.CallFailed;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Engine.Auth;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Application.Features.Workflows;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Titles;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Assembly.Infrastructure.Workflows;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Domain.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Topics;
using Assembly.Application.Features.Preferences;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Terminal;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;

namespace Assembly.Remote.Serve;

/// <summary>
/// Runs the remote host: the row store and sessions list, the live hub, the sessions, and the API a desktop copies the
/// rows through, until Ctrl+C. Every session here runs here.
/// </summary>
public static class ServeRun
{
    public static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (ServeOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine(ServeOptions.Usage);
            return 2;
        }

        return await RunAsync(options, null, null, null, default);
    }

    internal static async Task<int> RunAsync(ServeOptions options, IAgentLauncher? launcher,
        IEngineEffects? engineEffects, Action<Uri, EngineRuntime>? started, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Home);
        // Before the database opens, so a second host on this home writes nothing into the live one's; disposed last.
        using var homeLock = HomeLock.Take(options.Home);
        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var topics = new Topics();
        using var db = AppDatabase.Open(options.Database, TimeProvider.System, topics);
        WorkflowSchema.Open(db);
        var boardQueries = new WorkflowQueries(db);
        var boardTransitions = new WorkflowTransitions(db, TimeProvider.System);
        boardTransitions.Recover();
        var store = RowStore.Open(db);
        var summaries = SessionSummaries.Load(store);
        var sessionStore = SessionStore.Open(db, TimeProvider.System, options.Checkouts, summaries);
        var projectStore = new ProjectStore(db, TimeProvider.System);
        using var hub = new LiveHub(store, topics: topics, summaries: summaries, summaryChanged: sessionStore.PublishSummary);
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runsHere: _ => true);
        AdapterLauncher.MoveOldCheckouts(options.Home, options.Checkouts, Console.Out);
        var agents = launcher is null ? new AgentsFile(options.AgentsFile) : null;
        launcher ??= new AdapterLauncher(agents!, options.Checkouts, new UnclaimedPermissions());
        var preferences = new PreferenceStore(db);
        await using var work = new BackgroundWork(TimeProvider.System, Console.Out);
        await using var terminals = new TerminalHost(new UnixPtyStarter(), topics, TimeProvider.System,
            SessionTerminalFolders.ExistingFolder(options.Checkouts, sessionStore));
        using var hostStop = new CancellationTokenSource();
        var tokens = new McpTokens();
        var endpoint = new McpEndpointReady(tokens);
        var helperCards = new EngineHelperCards(db, TimeProvider.System, Console.Out, sessionStore);
        SessionRegistry? sessions = null;
        WorkflowReceiptServices? workflowReceipts = null;
        EngineRuntime? engine = null;
        GitOperations? gitOperations = null;
        NeedAnswers? answers = null;
        BoardTools? board = null;
        MachinesFeature? machinesFeature = null;
        MachineCalls? ownerMachineCalls = null;
        WebApplication? app = null;
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
                hostStop.Cancel();
                return stopped = Task.WhenAll(waits, runtime);
            }
        }
        try
        {
            var sessionLimits = new SessionLimits(
                () => SessionLimitPreferences.Depth(preferences.Read("sessions.depthLimit")),
                () => SessionLimitPreferences.Children(preferences.Read("sessions.childLimit")));
            var probeFolder = Path.Combine(options.Home, "probe");
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
            var owningHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var projectRoots = new ProjectRoots(projectStore);
            var cutover = new SessionCutover(db, sessionStore, launcher, options.Checkouts, TimeProvider.System, helperCards: helperCards,
                projectRoots: projectRoots, projectUse: projectRoots, owningHome: owningHome);
            var checkoutWrites = new CheckoutWrites();
            engine = EngineRuntime.Start(db, topics, TimeProvider.System, sessionLimits,
                cutover: cutover,
                effectsFactory: runtime =>
                {
                    agents?.BindOwner(runtime.Machine, topics);
                    sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, Console.Out, work,
                        titler: agents is null ? null : new SessionTitler(sessionStore, store, new CliUtilityModel(
                            AgentDescriptor.CliOf(agents, "claude"), AgentDescriptor.CliOf(agents, "codex"),
                            () => preferences.Read("utilityModel"), TimeProvider.System, Console.Out)),
                        feed: new EngineFeed(runtime, Console.Out, TimeProvider.System, helperCards),
                        mcp: McpSessionCallbacks.Create(runtime, endpoint, tokens));
                    sessions.BindHelperWorking(helperCards.Working, helperCards.Refresh);
                    workflowReceipts = WorkflowReceipts.Create(db, runtime.Machine, sessions);
                    if (engineEffects is not null) return engineEffects;
                    var catalogue = new AgentCatalogue(sessions, probeFolder, TimeProvider.System);
                    var runner = new SignInRunner(challenges, terminals, launcher, probeFolder, TimeProvider.System, Console.Out);
                    var signIn = new SignInEffects(db, runner, launcher, sessions, challenges, runtime, TimeProvider.System);
                    var proofRoot = Path.Combine(options.Home, "workflow-proof");
                    var workspace = new WorkspaceEffects(db, runtime.Commands, runtime.Machine, proofRoot,
                        new WorkflowWorkspace(proofRoot), TimeProvider.System);
                    var commands = new CommandStepEffects(db, runtime.Commands, runtime.Machine, proofRoot, TimeProvider.System,
                        async (folder, ct) => (await new ReceiptCheckout().Capture(folder, ct)).Fingerprint);
                    return new EffectRouter(new AdapterEffects(db, sessions, sessionStore, store))
                        .Add("checkout", new Assembly.Infrastructure.Engine.Checkouts.CheckoutEffects(db, new GitCli(), checkoutWrites, sessions,
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dev", "work", "worktrees")))
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
                    TimeProvider.System, Path.Combine(options.Home, "workflow-proof")).PrepareAsync,
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
            var attachments = new AttachmentStore(options.Attachments);
            var machines = new MachineSet(new LocalMachine(hub, sessionStore, registry, attachments));
            board = new BoardTools(sessionStore, registry, boardTransitions, boardQueries, hostStop.Token);
            var workflowCalls = new Assembly.Infrastructure.Workflows.WorkflowCalls(boardQueries, board.AnswerAsync);
            machinesFeature = new MachinesFeature(machines, new MachineStatus(topics, []), () => { }, TimeProvider.System);
            ownerMachineCalls = new MachineCalls(engine, TimeProvider.System, launcher, options.Checkouts, answers);
            var mcp = new McpServerHost(tokens, new McpRecorder(engine, gate, TimeProvider.System),
                [new StartSessionTool(gate, () => launcher.Agents.Select(agent => agent.Id).ToArray(), Directory.Exists),
                    new ListSessionsTool(gate), new SendMessageTool(gate), new MessageStatusTool(gate),
                    new AskOwnerTool(gate, answers), new AnswerNeedTool(gate, answers), new ReportTool(gate, workflowReceipts!.Capture),
                    new WorkflowFormatTool(gate), new CheckWorkflowTool(gate, WorkflowLoader.Load), new StartWorkflowTool(gate, WorkflowLoader.Load),
                    new LinkTaskTool(gate), new GetSessionTitleTool(gate), new BoardUpdateTool(gate, board)]);
            var sessionSources = new Assembly.Infrastructure.Engine.Sessions.SessionRowSources(db, sessionStore, machines);
            var door = new EngineDoor(engine, sessionStore, machines, attachments, TimeProvider.System, answers, hub, sessionSources);
            var git = new GitCli();
            var gh = new GhCli();
            gitOperations = new GitOperations(work, topics, TimeProvider.System, engine.Machine);
            var patches = new GitTextPatch(git);
            var prReader = new PullRequestReader(git, gh, options.Home, patches);
            var prWriter = new PullRequestWriter(git, gh, prReader);
            var sessionGit = new GitSessionOperations(sessionStore, git, prWriter, checkoutWrites, gitOperations);
            var localFolders = new LocalProjectFolders(git);
            var projectFolders = new ProjectFolderCreation(localFolders, new GitWriter(git), checkoutWrites, gitOperations);
            var gitRoutes = new SourceControlFeature(sessionStore, db, gitOperations, null, null);
            app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, mcp, db.Writer, topics,
                [new EngineCalls(engine, TimeProvider.System, answers, door), new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(engine, resolveProject: project => Assembly.Infrastructure.Engine.Workflows.WorkflowCalls.DefinitionRoot(new ProjectRoots(projectStore), project), projects: projectStore), new SessionTitleCalls(engine, sessionStore), new Assembly.Infrastructure.Engine.Sessions.SessionOpenCalls(db, engine, sessionStore, machines), ownerMachineCalls,
                    new ProvidersFeature(challenges, engine.Machine), new ProviderRuntimeCalls(engine.Machine, agents), new TerminalFeature(terminals), workflowCalls,
                    new RowsFeature(hub, store, sessionStore, machines, sessionSources), new SessionsFeature(sessionStore, machines, sessionSources, projectRoots, engine.Machine, owningHome, remoteFolders: localFolders),
                    new SendFeature(sessionStore, machines, hub, door), new ConfigFeature(sessionStore, machines, door), machinesFeature,
                    new AttachmentsFeature(sessionStore, machines, Path.Combine(options.Home, "staging", "attachments"), sessionSources),
                    new FilesFeature(sessionStore, new FileOwner(sessionStore, db), work, sources: sessionSources),
                    gitRoutes, new SourceControlWrites(sessionStore, git, checkoutWrites, TimeProvider.System, gitRoutes),
                    new PullRequestsFeature(sessionStore, prReader, gitRoutes), new FoldersFeature(localFolders),
                    new GitProjectOperations(gitOperations, projectFolders, sessionGit, gitRoutes)],
                board, Engine: engine), options.Port, out _);
            using var stopping = app.Lifetime.ApplicationStopping.Register(() => { _ = BeginStop(); });
            await app.StartAsync(cancellationToken);
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            endpoint.Listening(address);
            Console.WriteLine($"listening on {address}");
            Console.WriteLine($"home:  {options.Home}");
            Console.WriteLine($"token: in {options.TokenFile}");
            if (closed > 0) Console.WriteLine($"closed {closed} turn(s) the last run left unfinished");
            started?.Invoke(address, engine);
            await app.WaitForShutdownAsync(cancellationToken);
            return 0;
        }
        finally
        {
            var stopping = BeginStop();
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
            if (answers is not null) await answers.DisposeAsync();
            if (ownerMachineCalls is not null) await ownerMachineCalls.DisposeAsync();
            await stopping;
            if (board is not null) await board.DisposeAsync();
            if (gitOperations is not null) await gitOperations.DisposeAsync();
            if (machinesFeature is not null) await machinesFeature.DisposeAsync();
            await helperCards.DisposeAsync();
            if (sessions is not null) await sessions.DisposeAsync();
        }
    }
}
