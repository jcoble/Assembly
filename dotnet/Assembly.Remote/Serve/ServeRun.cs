using Assembly.Engine.Agents;
using Assembly.Engine.Api;
using Assembly.Engine.Attachments;
using Assembly.Engine.Features;
using Assembly.Engine.Live;
using Assembly.Engine.Machines;
using Assembly.Engine.Rows;
using Assembly.Engine.Sessions;
using Assembly.Engine.Store;
using Assembly.Engine.Threads;

namespace Assembly.Remote.Serve;

/// <summary>
/// Runs the remote host: the row store and sessions list, the live hub, the sessions, and the API a Mac copies the
/// rows through, until Ctrl+C. Every thread here runs here.
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
        var token = TokenFile.ReadOrCreate(options.TokenFile);
        using var store = RowStore.Open(options.Database);
        using var threads = ThreadStore.Open(options.Database, TimeProvider.System);
        using var hub = new LiveHub(store);
        var closed = RunRecovery.CloseUnfinished(store, hub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), runsHere: _ => true);
        var launcher = new AdapterLauncher(AgentDescriptor.Load(options.AgentsFile), options.Checkouts, new UnclaimedPermissions());
        await using var sessions = new SessionRegistry(hub, threads, launcher, TimeProvider.System, Console.Out);
        var attachments = new AttachmentStore(options.Attachments);

        var machines = new MachineSet(new LocalMachine(hub, threads, sessions, attachments));
        sessions.Helpers.Delivery.Machines = machines;

        // Sessions here join this machine's MCP server, on the API's port.
        sessions.Helpers.Tokens.Url = $"http://127.0.0.1:{options.Port}/mcp";
        await using var app = ApiServer.Build(new ApiServices(hub, threads, machines, attachments, token, sessions.Helpers), options.Port,
            routes => MachineFeatureList.Add(routes, new FeatureContext(options.Database)));
        Console.WriteLine($"listening on 127.0.0.1:{options.Port}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        if (closed > 0)
            Console.WriteLine($"closed {closed} turn(s) the last run left unfinished");
        await app.RunAsync();
        return 0;
    }
}
