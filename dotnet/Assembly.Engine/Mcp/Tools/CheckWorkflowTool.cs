using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Mcp.Tools;

public sealed class CheckWorkflowTool(McpGate gate, Func<string, string, CancellationToken, WorkflowSnapshot> load)
    : EngineTool("check_workflow", "Validate a workflow and its prompts without executing it. Project is an already-added exact root or unique title; omission uses your assigned project.",
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1},"project":{"type":"string","minLength":1}}}""")
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        WorkflowToolArguments.Validate(arguments, false);
        var args = arguments.Deserialize(McpJson.Default.CheckWorkflowArguments)!;
        var project = gate.Read(caller, (row, scope) => WorkflowToolProjects.Resolve(scope, gate.Machine, row.SessionId, args.Project), cancellationToken);
        CheckWorkflowAnswer answer;
        try
        {
            var source = load(project.RootPath, args.Path!, cancellationToken);
            var validated = WorkflowParser.Parse(new(source.Yaml, source.Prompts), cancellationToken);
            answer = new(true, validated.Hash, []);
        }
        catch (WorkflowFieldException error) { answer = new(false, null, [new(error.Field, error.Message)]); }
        gate.Read(caller, (_, _) => true, cancellationToken);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.CheckWorkflowAnswer));
    }
}
