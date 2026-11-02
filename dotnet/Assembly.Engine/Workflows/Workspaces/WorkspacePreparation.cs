using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Workflows.Workspaces;

internal sealed record WorkspaceBody(CommandContext Context, PrepareWorkspace Command, WorkspaceIntent Intent, string? SetupEffectId, string? StepKey);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceBody))]
internal sealed partial class WorkspacePreparationJson : JsonSerializerContext;

public static class WorkspacePreparation
{
    // Host-owned roots and checked Git head are captured outside the supplied writer, never from UI arguments.
    public static PreparedWorkflowCommand Prepare(IReadScope scope, CommandContext context, PrepareWorkspace command,
        string managedRoot, string proofRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var run = scope.QueryFirst("SELECT project_root,definition_hash,proof_dir FROM engine_run WHERE id=$id",
            r => new { Root = r.Text(0), Hash = r.Text(1), Proof = r.IsNull(2) ? null : r.Text(2) }, new SqlArg("$id", context.EntityId))
            ?? throw new InvalidOperationException("workflow_run_missing");
        if (!Path.IsPathFullyQualified(managedRoot) || !Path.IsPathFullyQualified(proofRoot) || !Path.IsPathFullyQualified(run.Root) ||
            !Guid.TryParseExact(context.EntityId, "N", out _) || !Guid.TryParseExact(command.AllocationId, "N", out _))
            throw new ArgumentException("invalid_workspace_allocation");
        var body = scope.QueryFirst("SELECT snapshot_json FROM engine_workflow_snapshot WHERE run_id=$id", r => r.Text(0), new SqlArg("$id", context.EntityId))!;
        var start = JsonSerializer.Deserialize(body, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        string? stepKey = null;
        if (command.StepId is not null)
        {
            stepKey = scope.QueryFirst("SELECT definition_key FROM engine_step WHERE id=$step AND run_id=$run",
                r => r.Text(0), new SqlArg("$step", command.StepId), new("$run", context.EntityId));
            if (stepKey is null || !start.Snapshot.Definition.Steps.TryGetValue(stepKey, out var spec) || spec.Workspace != command.Mode)
                throw new ArgumentException("invalid_workspace_step");
        }
        var branch = "run-" + context.EntityId[..8];
        if (start.Request.Task is { } task && task.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
        {
            var number = key.GetString()!.Split('-')[^1];
            if (number.Length > 0 && number.All(char.IsAsciiDigit)) branch = "tsk-" + number + "-" + context.EntityId[..8];
        }
        var repo = Path.GetFileName(Path.TrimEndingDirectorySeparator(run.Root));
        var directory = Path.Combine(Path.GetFullPath(managedRoot), repo, "run-" + context.EntityId);
        var path = Path.Combine(directory, "anchor");
        if (command.StepId is { } step)
        {
            if (!Guid.TryParseExact(step, "N", out _) || command.Generation < 0) throw new ArgumentException("invalid_workspace_allocation");
            path = Path.Combine(directory, step + "-" + command.Generation);
            branch += "-" + step[..8] + "-" + command.Generation;
        }
        var owned = command.Mode == "new";
        if (!owned) { path = run.Root; branch = ""; }
        var intent = new WorkspaceIntent(context.EntityId, command.StepId, command.Generation, command.AllocationId,
            run.Root, path, branch, command.BaseHead, owned, run.Proof ?? Path.Combine(Path.GetFullPath(proofRoot), command.AllocationId), run.Hash);
        var effect = command.StepId is null ? scope.QueryFirst("""
            SELECT id FROM engine_outbox WHERE run_id=$id AND kind='workflow_setup' AND state='inflight'
            """, r => r.Text(0), new SqlArg("$id", context.EntityId)) : null;
        return WorkflowPreparation.Freeze(new WorkspaceBody(context with { NowMs = 0 }, command, intent, effect, stepKey), ct);
    }
}
