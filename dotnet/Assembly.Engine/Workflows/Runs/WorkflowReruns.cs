using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Workflows.Runs;

internal sealed record WorkflowRerunSource(string RunId, long Version, bool FailedOnly);

public static partial class WorkflowStarts
{
    /// <summary>Copies the immutable definition through the one prepared start seam, before entering the writer.</summary>
    public static PreparedWorkflowCommand PrepareRerun(IReadScope s, CommandContext context, RerunWorkflow command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var source = s.QueryFirst("""
            SELECT r.version,r.state,r.machine,snap.snapshot_json FROM engine_run r
            JOIN engine_workflow_snapshot snap ON snap.run_id=r.id WHERE r.id=$id
            """, r => new { Version = r.Long(0), State = r.Text(1), Machine = r.Text(2), Body = r.Text(3) }, new SqlArg("$id", command.SourceRunId))
            ?? throw new ArgumentException("workflow_run_not_found");
        var original = JsonSerializer.Deserialize(source.Body, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var request = original.Request with
        {
            CommandId = context.CommandId,
            ProposedRunId = context.EntityId,
            ExpectedVersion = context.ExpectedVersion,
            Epoch = context.Epoch,
            RequestKey = command.RequestKey,
            Machine = context.Machine
        };
        var failedOnly = command is RetryFailedWorkflow;
        var fingerprint = WorkflowPreparation.RequestFingerprint(request, original.ProjectId, original.ProjectRoot) + "/" + command.SourceRunId + "/" + (failedOnly ? "failed" : "all");
        var existing = Read(s, request);
        var basis = existing is null ? original : JsonSerializer.Deserialize(existing.Body, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var ids = existing is null ? basis.Snapshot.Definition.Steps.Keys.ToImmutableSortedDictionary(key => key, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal) : basis.StepIds;
        return WorkflowPreparation.Freeze(basis with
        {
            Context = context with { NowMs = 0 },
            Request = request,
            StepIds = ids,
            RequestFingerprint = fingerprint,
            Rerun = existing is not null && existing.Fingerprint == fingerprint ? basis.Rerun : new(command.SourceRunId, source.Version, failedOnly)
        }, ct);
    }
}
