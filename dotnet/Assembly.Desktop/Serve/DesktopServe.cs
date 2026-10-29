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

        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var agents = AgentDescriptor.Parse(File.ReadAllText(options.AgentsFile));
        var launcher = new AdapterLauncher(agents, options.Checkouts, new UnclaimedPermissions());
        var profile = options.Remote is { } host ? new SavedMachine(host, host, options.RemotePort, options.RemoteTokenFile) : null;
        await using var backend = await DesktopBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out,
            utilityClis: (AgentDescriptor.CliOf(agents, "claude"), AgentDescriptor.CliOf(agents, "codex")), savedRemote: profile);
        // The window's launcher waits for this line's "listening on " prefix: it comes once /socket takes a hello and
        // the MCP url is set. Remote connection attempts run independently of local startup.
        Console.WriteLine($"listening on {backend.Address}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        if (options.ExitOnStdinClose)
            backend.StopAtEndOf(Console.OpenStandardInput());
        await backend.WaitForShutdownAsync();
        return 0;
    }
}
