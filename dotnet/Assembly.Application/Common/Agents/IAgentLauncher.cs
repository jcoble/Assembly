using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Agents;

/// <summary>Checks out a thread's repo and starts adapter processes. Tests swap in a scripted agent.</summary>
public interface IAgentLauncher
{
    /// <summary>Clones the thread's repo into its own folder if it isn't there yet, and returns the folder. Marks the clone.</summary>
    Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken);

    /// <summary>The agents this machine can run.</summary>
    IReadOnlyList<AgentDescriptor> Agents { get; }

    /// <summary>Starts the agent's adapter process, which its threads' sessions then share. Marks the adapter start.</summary>
    Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken);
}
