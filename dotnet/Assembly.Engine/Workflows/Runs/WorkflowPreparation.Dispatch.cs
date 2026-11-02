using System.Text.Json;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowPreparation
{
    internal static PreparedWorkflowDispatch Freeze(DispatchBody body, CancellationToken ct)
    {
        var context = body.Context;
        body = body with { Context = context with { NowMs = 0 } };
        var value = JsonSerializer.SerializeToElement(body, DispatchJson.Default.DispatchBody);
        var (json, sha, bytes) = Digest(value, ct);
        var frozen = JsonSerializer.Deserialize(json, DispatchJson.Default.DispatchBody)!;
        return new(frozen, new PreparedWorkflowCommand(frozen, json, sha, bytes), context: context);
    }
}
