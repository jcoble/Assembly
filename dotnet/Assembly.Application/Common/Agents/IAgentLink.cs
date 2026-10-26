using Assembly.Application.Common.Acp;

namespace Assembly.Application.Common.Agents;

/// <summary>A running agent: its ACP client and its exit. Disposing ends it.</summary>
public interface IAgentLink : IAsyncDisposable
{
    AcpClient Client { get; }

    /// <summary>Completes when the agent's process ends, for any reason.</summary>
    Task Exited { get; }
}
