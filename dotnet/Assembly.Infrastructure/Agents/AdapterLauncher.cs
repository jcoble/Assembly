using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

/// <summary>Checks sessions out in <c>checkouts/&lt;session id&gt;</c> (a helper uses its parent's), and starts an agent's ACP adapter in <c>checkouts</c>.</summary>
public sealed class AdapterLauncher(IReadOnlyList<AgentDescriptor> agents, string checkouts, IPermissionPolicy permissions) : IAgentLauncher
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
        var folder = FolderOf(checkouts, session);
        await GitCheckout.EnsureAsync(session.RepoUrl, folder, cancellationToken);
        stages.Mark("clone");
        return folder;
    }

    /// <summary>A session's checkout folder, once it exists: what its terminal starts in.</summary>
    public static Func<string, string?> ExistingFolder(string checkouts, SessionStore sessions) => id =>
        sessions.Get(id) is { } session && FolderOf(checkouts, session) is var folder && Directory.Exists(folder) ? folder : null;

    // A helper works in its parent's folder, as an agent's own subagents do.
    private static string FolderOf(string checkouts, SessionInfo session) =>
        Path.Combine(checkouts, session is { Helper: true, ParentSessionId: { } parent } ? parent : session.SessionId);

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    {
        var descriptor = agents.FirstOrDefault(agent => agent.Id == agentId)
            ?? throw new InvalidOperationException($"This machine has no agent {agentId}.");
        // Each ACP session gets its session's folder as its cwd; the process itself just needs somewhere to start.
        Directory.CreateDirectory(checkouts);
        var agent = AgentConnection.Start(descriptor.ToCommand(checkouts), permissions, TranscriptLog.Discard());
        stages.Mark("adapter");
        return Task.FromResult<IAgentLink>(agent);
    }
}
