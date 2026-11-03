using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Mcp;

public sealed partial class McpWrite
{
    internal StartWorkflowAnswer ApplyWorkflowStart(PreparedWorkflowCommand prepared)
    {
        var start = prepared.Start;
        if (Caller.ParentId is not null || Caller.StepId is not null) throw new McpRefused(new("owner_reserved"));
        if (start.Request.OwnerSessionId != Caller.SessionId || start.Context.Actor.Id != Caller.SessionId || start.Context.Epoch != Caller.Epoch)
            throw new McpRefused(new("stale_epoch"));
        var ack = WorkflowStarts.Apply(Scope, _runtime.Commands, start.Context with { NowMs = _time.GetUtcNow().ToUnixTimeMilliseconds() }, prepared);
        if (!ack.Result.Applied) throw new McpRefused(new(ack.Result.Error ?? "bad_args"));
        var state = Scope.QueryFirst("SELECT state FROM engine_run WHERE id=$run", r => r.Text(0), new SqlArg("$run", ack.RunId))!;
        return new(ack.RunId, state, ack.DefinitionHash);
    }
}
