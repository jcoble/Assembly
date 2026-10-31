using Assembly.Application.Features.Workflows;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Infrastructure.Engine;
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
        var sessionStore = SessionStore.Open(db, TimeProvider.System, options.Checkouts);
        using var hub = new LiveHub(store);
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runsHere: _ => true);
        AdapterLauncher.MoveOldCheckouts(options.Home, options.Checkouts, Console.Out);
        var agents = launcher is null ? AgentDescriptor.Parse(File.ReadAllText(options.AgentsFile)) : null;
        launcher ??= new AdapterLauncher(agents!, options.Checkouts, new UnclaimedPermissions());
        var limits = new HelperLimits();
        // Stopped after the sessions, before the hub and the database.
        await using var work = new BackgroundWork(TimeProvider.System, Console.Out);
        await using var sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, Console.Out, work, limits,
            titler: agents is null ? null : new SessionTitler(sessionStore, store, new CliUtilityModel(AgentDescriptor.CliOf(agents, "claude"), AgentDescriptor.CliOf(agents, "codex"), new PreferenceStore(db), TimeProvider.System, Console.Out)));
        var attachments = new AttachmentStore(options.Attachments);

        var machines = new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments));
        sessions.Helpers.Delivery.Machines = machines;
        using var hostStop = new CancellationTokenSource();
        await using var board = new BoardTools(sessionStore, sessions, boardTransitions, boardQueries, hostStop.Token);
        var workflowCalls = new WorkflowCalls(boardQueries, board.AnswerAsync);

        // Sessions here join this machine's MCP server, on the API's port.
        sessions.Helpers.Tokens.Url = $"http://127.0.0.1:{options.Port}/mcp";
        await using var terminals = new TerminalHost(new UnixPtyStarter(), topics, TimeProvider.System,
            AdapterLauncher.ExistingFolder(options.Checkouts, sessionStore));
        await using var engine = EngineRuntime.Start(db, topics, TimeProvider.System, () => limits.UnfinishedHelpers, engineEffects);
        await using var app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, sessions.Helpers, db.Writer, topics, [new EngineCalls(engine, TimeProvider.System), new TerminalFeature(terminals), workflowCalls], board, Engine: engine), options.Port, out _,
            routes => MachineFeatureList.Add(routes, new FeatureContext(db)));
        Console.WriteLine($"listening on 127.0.0.1:{options.Port}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        if (closed > 0)
            Console.WriteLine($"closed {closed} turn(s) the last run left unfinished");
        using var stopping = app.Lifetime.ApplicationStopping.Register(hostStop.Cancel);
        try
        {
            await app.StartAsync(cancellationToken);
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            sessions.Helpers.Tokens.Url = new Uri(address, "/mcp").ToString();
            started?.Invoke(address, engine);
            await app.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await hostStop.CancelAsync();
        }
        return 0;
    }
}
