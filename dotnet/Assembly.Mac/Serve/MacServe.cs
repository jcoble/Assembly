using Assembly.Engine.Agents;
using Assembly.Engine.Api;
using Assembly.Engine.Sessions;
using Assembly.Mac.Remote;

namespace Assembly.Mac.Serve;

/// <summary>Runs the Mac's backend until Ctrl+C, with a remote machine when <c>--remote</c> names one.</summary>
public static class MacServe
{
    public static async Task<int> MainAsync(IReadOnlyList<string> args)
    {
        if (MacOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine(MacOptions.Usage);
            return 2;
        }

        var token = TokenFile.ReadOrCreate(options.TokenFile);
        var launcher = new AdapterLauncher(AgentDescriptor.Load(options.AgentsFile), options.Checkouts, new UnclaimedPermissions());
        var (tunnel, remote) = options.Remote is { } host ? await RemoteTunnel.ConnectAsync(host, options.RemotePort, options.RemoteTokenFile, Console.Out) : (null, null);
        await using var stopTunnel = tunnel;
        await using var backend = await MacBackend.StartAsync(options.Home, token, options.Port, launcher, Console.Out, remote);
        Console.WriteLine($"listening on {backend.Address}");
        Console.WriteLine($"home:  {options.Home}");
        Console.WriteLine($"token: in {options.TokenFile}");
        await backend.WaitForShutdownAsync();
        return 0;
    }
}
