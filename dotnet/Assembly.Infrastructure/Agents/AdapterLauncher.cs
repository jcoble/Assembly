using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

/// <summary>Checks sessions out in <c>checkouts/&lt;session id&gt;</c> (a helper uses its parent's), and starts an agent's ACP adapter in <c>checkouts</c>.</summary>
public sealed partial class AdapterLauncher(IReadOnlyList<AgentDescriptor> agents, string checkouts, IPermissionPolicy permissions,
    Func<SessionInfo, string?> checkoutFolder) : IAgentLauncher
{
    public IReadOnlyList<AgentDescriptor> Agents => agents;

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

    public async Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken)
    {
        var folder = checkoutFolder(session) ?? throw new InvalidOperationException($"{session.SessionId} has no folder");
        await GitCheckout.EnsureAsync(session.RepoUrl, folder, cancellationToken);
        stages.Mark("clone");
        return folder;
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
