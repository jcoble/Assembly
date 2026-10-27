using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// The machines a new session can run on, over the socket (plan C1): <c>machines/list</c> names the remote ones,
/// besides this one, and <c>machines/agents</c> answers the agents a machine (this one when left out) can run.
/// </summary>
internal sealed class MachinesFeature : IFeature
{
    public const string Feature = "machines";

    private readonly MachineSet _machines;

    public MachinesFeature(MachineSet machines) => _machines = machines;

    public void Register(ICallRegistry calls)
    {
    }

    internal sealed record ListArgs;

    internal sealed record MachineList(IReadOnlyList<string> Machines);

    internal sealed record AgentsArgs(string? Machine = null);

    internal sealed record Agent(string Id, string Name);

    internal sealed record AgentList(IReadOnlyList<Agent> Agents);
}
