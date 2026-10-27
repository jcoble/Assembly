using Assembly.Infrastructure.Persistence;
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

        Directory.CreateDirectory(options.Home);
        // Before the database opens, so a second host on this home writes nothing into the live one's; disposed last.
        using var homeLock = HomeLock.Take(options.Home);
        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var topics = new Topics();
        using var db = AppDatabase.Open(options.Database, TimeProvider.System, topics);
        var store = RowStore.Open(db);
        var sessionStore = SessionStore.Open(db, TimeProvider.System);
        using var hub = new LiveHub(store);
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runsHere: _ => true);
        AdapterLauncher.MoveOldCheckouts(options.Home, options.Checkouts, Console.Out);
        var launcher = new AdapterLauncher(AgentDescriptor.Parse(File.ReadAllText(options.AgentsFile)), options.Checkouts, new UnclaimedPermissions());
        await using var sessions = new SessionRegistry(hub, sessionStore, launcher, TimeProvider.System, Console.Out);
        var attachments = new AttachmentStore(options.Attachments);

        var machines = new MachineSet(new LocalMachine(hub, sessionStore, sessions, attachments));
        sessions.Helpers.Delivery.Machines = machines;

        // Sessions here join this machine's MCP server, on the API's port.
        sessions.Helpers.Tokens.Url = $"http://127.0.0.1:{options.Port}/mcp";
        await using var app = ApiServer.Build(new ApiServices(hub, sessionStore, machines, attachments, token, sessions.Helpers, db.Writer, topics), options.Port,
            routes => MachineFeatureList.Add(routes, new FeatureContext(db)));
        Console.WriteLine($"listening on 127.0.0.1:{options.Port}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        if (closed > 0)
            Console.WriteLine($"closed {closed} turn(s) the last run left unfinished");
        await app.RunAsync();
        return 0;
    }
}
