using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Mcp.Tools;

public sealed class StartWorkflowTool(McpGate gate, Func<string, string, CancellationToken, WorkflowSnapshot> load)
    : EngineTool("start_workflow", "Start a workflow on behalf of your top-level session. Reuse the SAME requestKey after a timeout to get the original run. Project is an already-added exact root or unique title; omission uses your assigned project.",
        """
        {"type":"object","additionalProperties":false,"required":["path","request","requestKey"],"properties":{
          "path":{"type":"string","minLength":1},"request":{"type":"string","minLength":1},"requestKey":{"type":"string","minLength":1},
          "project":{"type":"string","minLength":1},"inputs":{"type":"object"},
          "task":{"type":"object","additionalProperties":false,"required":["key","title","body"],"properties":{
            "key":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"},"url":{"type":"string","format":"uri"}}}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        WorkflowToolArguments.Validate(arguments, true);
        var args = arguments.Deserialize(McpJson.Default.StartWorkflowArguments)!;
        using var empty = JsonDocument.Parse("{}");
        var original = gate.Read(caller, (row, scope) =>
        {
            if (row.ParentId is not null || row.StepId is not null) throw new McpRefused(new("owner_reserved"));
            var project = WorkflowToolProjects.Resolve(scope, gate.Machine, row.SessionId, args.Project);
            var context = new CommandContext(Guid.NewGuid(), gate.Machine, new(ActorKind.Session, row.SessionId), Guid.NewGuid().ToString("N"), 0, row.Epoch, 0);
            var request = new WorkflowStartRequest(context.CommandId, context.EntityId, 0, row.Epoch, row.SessionId,
                args.RequestKey!, args.Path!, args.Project, args.Request!, args.Task, args.Inputs ?? empty.RootElement);
            return (Project: project, Context: context, Request: request, Existing: WorkflowStarts.PrepareExisting(scope, context, request, project, cancellationToken));
        }, cancellationToken);
        var prepared = original.Existing;
        try
        {
            prepared ??= WorkflowStarts.Prepare(original.Context, original.Request,
                load(original.Project.RootPath, args.Path!, cancellationToken), original.Project, cancellationToken);
        }
        catch (WorkflowFieldException error) { throw WorkflowToolArguments.Bad(error.Field, error.Message); }
        var answer = gate.Write(caller, write => write.ApplyWorkflowStart(prepared));
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.StartWorkflowAnswer));
    }
}
