using Assembly.Engine.Mcp.Tools;

namespace Assembly.Engine.Mcp;

/// <summary>Completes one authenticated remote helper start and its durable owning initial prompt.</summary>
public interface IMcpMachineStarts
{
    Task<StartSessionAnswer> StartAsync(McpCaller caller, StartSessionArguments arguments, CancellationToken cancellationToken);
}
