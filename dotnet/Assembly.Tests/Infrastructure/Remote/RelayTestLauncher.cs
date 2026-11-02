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
    public Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken)
    { Interlocked.Increment(ref ProviderCalls); throw new InvalidOperationException("No provider expected"); }
    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    { Interlocked.Increment(ref ProviderCalls); throw new InvalidOperationException("No provider expected"); }
}
