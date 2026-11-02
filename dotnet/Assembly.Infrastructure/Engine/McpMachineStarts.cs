using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;

namespace Assembly.Infrastructure.Engine;

internal sealed class McpMachineStarts(MachineStarts starts) : IMcpMachineStarts
{
    public Task<StartSessionAnswer> StartAsync(McpCaller caller, StartSessionArguments arguments, CancellationToken cancellationToken) =>
        starts.StartMcpAsync(caller, arguments, cancellationToken);
}
