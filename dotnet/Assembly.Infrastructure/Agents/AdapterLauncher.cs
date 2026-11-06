using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

/// <summary>Starts configured ACP adapters; session checkout preparation belongs to the Engine.</summary>
public sealed partial class AdapterLauncher(IReadOnlyList<AgentDescriptor> agents, string checkouts, IPermissionPolicy permissions) : IAgentLauncher
{
    public IReadOnlyList<AgentDescriptor> Agents => agents.ToArray();

    /// <summary>
    /// Checkouts were in <c>home/threads</c> until sessions were renamed: moves that folder to <paramref name="checkouts"/>
    /// when only the old one exists. When both do, both are left as they are and the log says so.
    /// </summary>
    public static void MoveOldCheckouts(string home, string checkouts, TextWriter log)
    {
        var old = Path.Combine(home, "threads");
        if (!Directory.Exists(old))
            return;
        if (Directory.Exists(checkouts))
        {
            log.WriteLine($"checkouts: both {old} and {checkouts} exist; left both as they are");
            return;
        }

        Directory.Move(old, checkouts);
        log.WriteLine($"checkouts: moved {old} to {checkouts}");
    }

    public void ValidateFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new CallFailed(File.Exists(folder) ? $"This session's folder isn't a folder: {folder}" : $"This session's folder no longer exists: {folder}");
    }

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    {
        var captured = ConfigTargetIdentity.Capture(this, agentId);
        return LaunchConfiguredAsync(captured.Descriptor, captured.Environment, stages, cancellationToken);
    }
}
