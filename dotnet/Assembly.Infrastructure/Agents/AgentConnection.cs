using Assembly.Application.Common.Acp;
using Assembly.Infrastructure.Transcript;

using Assembly.Application.Common.Agents;
namespace Assembly.Infrastructure.Agents;

/// <summary>
/// An adapter process and the ACP client talking to it, owned together so they shut down in the right
/// order: end the process first (its exit closes the pipe), then let the client's read loop finish.
/// </summary>
public sealed class AgentConnection : IAgentLink
{
    private readonly AgentProcess _process;

    private AgentConnection(AgentProcess process, AcpClient client)
    {
        _process = process;
        Client = client;
    }

    public AcpClient Client { get; }

    public int ProcessId => _process.Id;

    /// <summary>Completes when the adapter process ends. Mid-turn, that's the lost-run path.</summary>
    public Task Exited => _process.Exited;

    public void Kill() => _process.Kill();

    public static AgentConnection Start(AgentCommand command, IPermissionPolicy permissions, TranscriptLog transcript)
    {
        var process = AgentProcess.Start(command, transcript);
        var client = new AcpClient(process.Output, process.Input, permissions, transcript);
        return new AgentConnection(process, client);
    }

    public async ValueTask DisposeAsync()
    {
        await _process.DisposeAsync();
        await Client.DisposeAsync();
    }
}
