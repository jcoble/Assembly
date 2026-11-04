using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Infrastructure.Remote;

internal sealed class RelayTestLauncher : IAgentLauncher
{
    internal int ProviderCalls;
    public IReadOnlyList<AgentDescriptor> Agents => [new("codex", "Codex", "unused", [], null)];
    public void ValidateFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new CallFailed("Invalid folder");
    }
    public bool CanRunTerminalAuthentication => false;
    public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        StageTimes stages, CancellationToken cancellationToken) => LaunchAsync(descriptor.Id, stages, cancellationToken);
    public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
        Assembly.Application.Common.Acp.IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) =>
        LaunchAsync(descriptor.Id, stages, cancellationToken);

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    { Interlocked.Increment(ref ProviderCalls); throw new InvalidOperationException("No provider expected"); }
}
