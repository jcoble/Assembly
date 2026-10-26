using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Infrastructure.Agents;

/// <summary>Checks threads out in <c>checkouts/&lt;thread id&gt;</c> (a helper uses its parent's), and starts an agent's ACP adapter in <c>checkouts</c>.</summary>
public sealed class AdapterLauncher(IReadOnlyList<AgentDescriptor> agents, string checkouts, IPermissionPolicy permissions) : IAgentLauncher
{
    public IReadOnlyList<AgentDescriptor> Agents => agents;

    public async Task<string> CheckoutAsync(ThreadInfo thread, StageTimes stages, CancellationToken cancellationToken)
    {
        // A helper works in its parent's folder, as an agent's own subagents do.
        var folder = Path.Combine(checkouts, thread is { Helper: true, ParentThreadId: { } parent } ? parent : thread.ThreadId);
        await GitCheckout.EnsureAsync(thread.RepoUrl, folder, cancellationToken);
        stages.Mark("clone");
        return folder;
    }

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    {
        var descriptor = agents.FirstOrDefault(agent => agent.Id == agentId)
            ?? throw new InvalidOperationException($"This machine has no agent {agentId}.");
        // Each session gets its thread's folder as its cwd; the process itself just needs somewhere to start.
        Directory.CreateDirectory(checkouts);
        var agent = AgentConnection.Start(descriptor.ToCommand(checkouts), permissions, TranscriptLog.Discard());
        stages.Mark("adapter");
        return Task.FromResult<IAgentLink>(agent);
    }
}
