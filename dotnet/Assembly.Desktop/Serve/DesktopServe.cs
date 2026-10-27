using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Mcp;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Remote;

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

        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var launcher = new AdapterLauncher(AgentDescriptor.Parse(File.ReadAllText(options.AgentsFile)), options.Checkouts, new UnclaimedPermissions());
        var (tunnel, remote) = options.Remote is { } host ? await RemoteTunnel.ConnectAsync(host, options.RemotePort, options.RemoteTokenFile, Console.Out) : (null, null);
        await using var stopTunnel = tunnel;
        await using var backend = await DesktopBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out, remote, tunnel);
        // The window's launcher waits for this line's "listening on " prefix: it comes once /socket takes a hello and
        // the MCP url is set. A failed or hung tunnel, or a port in use, never prints it, so the launcher also watches
        // the exit and limits its wait.
        Console.WriteLine($"listening on {backend.Address}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        await backend.WaitForShutdownAsync();
        return 0;
    }
}
