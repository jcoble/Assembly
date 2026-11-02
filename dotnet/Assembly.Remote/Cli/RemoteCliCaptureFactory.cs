using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Updates;
using Assembly.Remote.Serve;

namespace Assembly.Remote.Cli;

/// <summary>Publishes only this owning host's real startup configuration at an explicitly selected private location.</summary>
internal static class RemoteCliCaptureFactory
{
    internal static async Task<RemoteCliCaptureData> PublishAsync(ServeOptions options, IAgentLauncher launcher,
        string machineId, string installationId, string ownerChosenCaptureFile, CancellationToken cancellationToken)
    {
        var command = Invocation(Environment.ProcessPath, Environment.GetCommandLineArgs()[0], options.Home);
        var capture = RemoteCliCapture.Capture(launcher, machineId, installationId,
            options.Home, options.AgentsFile, command);
        await RemoteCliCapture.SaveAsync(ownerChosenCaptureFile, capture, cancellationToken);
        return capture;
    }

    // The dotnet muxer needs its entry DLL. An apphost/native executable already owns its entrypoint,
    // even when GetCommandLineArgs()[0] reports a managed DLL. Never carry serve arguments forward.
    internal static AgentCommand Invocation(string? processPath, string entryAssembly, string home)
    {
        if (processPath is null || !Path.IsPathFullyQualified(processPath) || !File.Exists(processPath) ||
            !Path.IsPathFullyQualified(entryAssembly) || !File.Exists(entryAssembly) ||
            !Path.IsPathFullyQualified(home)) throw RemoteCliCapture.Unavailable();
        var dotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (dotnetHost && !entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw RemoteCliCapture.Unavailable();
        return new(processPath, dotnetHost ? [entryAssembly] : [], home);
    }
}
