using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

/// <summary>Checks sessions out in <c>checkouts/&lt;session id&gt;</c> (a helper uses its parent's), and starts an agent's ACP adapter in <c>checkouts</c>.</summary>
public sealed class AdapterLauncher(IReadOnlyList<AgentDescriptor> agents, string checkouts, IPermissionPolicy permissions) : IAgentLauncher
{
    public IReadOnlyList<AgentDescriptor> Agents => agents;
    public IReadOnlyList<ConfigTarget> ConfigTargets => agents.Select(agent => new ConfigTarget(agent.Id, agent.Name,
        ConfigTargetIdentity.Of(agent, ConfigTargetIdentity.EnvironmentOf(agent)))).ToList();

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
        var folder = SessionStore.Folder(checkouts, session) ?? throw new InvalidOperationException($"{session.SessionId} has no folder");
        if (session.WorkingFolder is not null) ValidateFolder(folder);
        else await GitCheckout.EnsureAsync(session.RepoUrl, folder, cancellationToken);
        stages.Mark("clone");
        return folder;
    }

    /// <summary>
    /// A session's checkout folder, once it exists: what its terminal starts in. Fails for a session on another
    /// machine (a helper is on its parent's): its folder is there, and its terminal can't be reached from here yet.
    /// </summary>
    public static Func<string, string?> ExistingFolder(string checkouts, SessionStore sessions) => id =>
        sessions.Get(id) switch
        {
            { Machine: { Length: > 0 } machine } => throw new CallFailed($"The terminal for a session on {machine} can't be opened here yet."),
            { } session when SessionStore.Folder(checkouts, sessions.Top(session)) is { } folder && Directory.Exists(folder) => folder,
            null => throw new CallFailed($"Unknown terminal owner: {id}"),
            _ => null,
        };

    public void ValidateFolder(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new IOException();
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new CallFailed("Choose an existing readable absolute working folder on the selected machine."); }
    }

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    {
        var captured = ConfigTargetIdentity.Capture(this, agentId);
        return LaunchConfiguredAsync(captured.Descriptor, captured.Environment, stages, cancellationToken);
    }

    public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        StageTimes stages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigTargetIdentity.Capture(this, descriptor.Id, ConfigTargetIdentity.Of(descriptor, effectiveEnvironment));
        Directory.CreateDirectory(checkouts);
        var agent = AgentConnection.Start(new AgentCommand(descriptor.Command, descriptor.Args, checkouts, effectiveEnvironment, ReplaceEnvironment: true), permissions, TranscriptLog.Discard());
        stages.Mark("adapter");
        return Task.FromResult<IAgentLink>(agent);
    }
}
