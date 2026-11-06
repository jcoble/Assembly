using Assembly.Application.Common.Acp;

namespace Assembly.Application.Common.Agents;

/// <summary>Starts configured adapter processes and validates existing folders. Checkout belongs to the Engine.</summary>
public interface IAgentLauncher
{
    /// <summary>Validates an absolute existing direct working folder, using the ordinary plain folder failure.</summary>
    void ValidateFolder(string folder);

    /// <summary>The agents this machine can run.</summary>
    IReadOnlyList<AgentDescriptor> Agents { get; }

    /// <summary>Starts the agent's adapter process, which its sessions' ACP sessions then share. Marks the adapter start.</summary>
    Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken);

    /// <summary>Whether this owner can reproduce its configured command in its direct program terminal.</summary>
    bool CanRunTerminalAuthentication { get; }

    Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        StageTimes stages, CancellationToken cancellationToken);

    Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken);
}
