using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Mcp.Tools;

public sealed class WorkflowFormatTool(McpGate gate) : EngineTool("workflow_format", "Read the current workflow format and receipt requirements.",
    """{"type":"object","additionalProperties":false,"properties":{}}""")
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        WorkflowToolArguments.Object(arguments, "arguments", []);
        var answer = gate.Read(caller, (_, _) => new WorkflowFormatAnswer(1, WorkflowFormat.Text), cancellationToken);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.WorkflowFormatAnswer));
    }
}
