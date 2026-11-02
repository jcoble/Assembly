using Assembly.Engine.Workflows;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Mcp;

public sealed partial class McpWrite
{
    internal CommandResult ApplyWorkflowReceipt(PreparedWorkflowCommand prepared)
    {
        var body = prepared.Receipt ?? throw new ArgumentException("Not a receipt preparation.", nameof(prepared));
        if (body.Capture.SessionId != Caller.SessionId || body.Capture.Epoch != Caller.Epoch)
            throw new McpRefused(new("stale_epoch"));
        var result = _runtime.Commands.ApplyPreparedWorkflow(Scope,
            body.Context with { NowMs = _time.GetUtcNow().ToUnixTimeMilliseconds() }, prepared);
        if (!result.Applied) throw new McpRefused(new(result.Error ?? "receipt_refused"));
        return result;
    }
}
