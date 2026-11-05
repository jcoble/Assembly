using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Machines;

namespace Assembly.Desktop.Serve;

/// <summary>Runs the desktop backend until Ctrl+C, with a remote machine when <c>--remote</c> names one.</summary>
public static class DesktopServe
{
    /// <summary>The update helper has this home folder for a moment. The window's launcher knows this code; any other holder exits 1.</summary>
    public const int HomeInUseExit = 75;

    /// <summary>
    /// The update helper's check after an install on quit: this version runs and opens the database, migrating it, with no
    /// window and nothing else started. Non-zero puts the old version back.
    /// </summary>
    public static int CheckStart(string database)
    {
        try
        {
            using var db = Assembly.Infrastructure.Persistence.AppDatabase.Open(database, TimeProvider.System, new Assembly.Infrastructure.Topics.Topics());
            return 0;
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Assembly can't open {database}: {error.Message}");
            return 1;
        }
    }

    public static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (DesktopOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine(DesktopOptions.Usage);
            return 2;
        }
        if (options.Remote == "local")
        {
            Console.Error.WriteLine("a remote can't be named local, the key for this computer");
            return 2;
        }

        try { return await ServeAsync(options); }
        catch (Assembly.Infrastructure.Persistence.HomeInUseException refused)
        {
            Console.Error.WriteLine($"Assembly can't open {options.Home}: {refused.Message}");
            return refused.Update ? HomeInUseExit : 1;
        }
    }

    private static async Task<int> ServeAsync(DesktopOptions options)
    {
        var token = TokenFile.ReadOrCreate(options.TokenFile);
        if (AgentsFile.OpenOrSay(options.AgentsFile, Console.Error) is not { } agents) return 1;
        var launcher = new AdapterLauncher(agents, options.Checkouts, new UnclaimedPermissions());
        var profile = options.Remote is { } host ? new SavedMachine(host, host, options.RemotePort, options.RemoteTokenFile) : null;
        await using var backend = await DesktopBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out,
            utilityClis: (AgentDescriptor.CliOf(agents, "claude"), AgentDescriptor.CliOf(agents, "codex")), savedRemote: profile, agentsFile: agents);
        // The window's launcher waits for this line's "listening on " prefix: it comes once /socket takes a hello and
        // the MCP url is set. Remote connection attempts run independently of local startup.
        backend.WatchFirstStart();
        backend.BeginUpdateChecks();
        Console.WriteLine($"listening on {backend.Address}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        if (options.ExitOnStdinClose)
            backend.StopAtEndOf(Console.OpenStandardInput());
        await backend.WaitForShutdownAsync();
        return 0;
    }
}
