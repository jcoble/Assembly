using System.Globalization;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    /// <summary>The host calls this only after the exact original execution is fully released, never on idle alone.</summary>
    public static PreparedWorkflowRelease? PrepareWorkflowRelease(IReadScope scope, SessionObservation observed, FinalizedChatReply source)
    {
        if (observed.StepId is null || observed.RunId is null || observed.Attempt is null) return null;
        if (observed.SessionId != source.SessionId || observed.Epoch < 0 || source.TerminalSeq <= 0 ||
            string.IsNullOrWhiteSpace(source.ChatRunId) || source.TerminalState is not ("finished" or "failed" or "cancelled"))
            throw new ArgumentException("The original workflow execution terminal identity changed.", nameof(source));
        var original = scope.QueryFirst("""
            SELECT st.result,snapshot.definition_hash,a.generation,a.authority_machine,
              EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.stepIds') ids WHERE ids.key=st.definition_key AND ids.value=st.id),
              EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.snapshot.definition.steps') spec
                WHERE spec.key=st.definition_key AND json_extract(spec.value,'$.result')=st.result)
            FROM engine_step st LEFT JOIN engine_workflow_snapshot snapshot ON snapshot.run_id=st.run_id
            LEFT JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=$attempt AND a.session_id=$session
              AND a.execution_epoch=$epoch AND a.authority_machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
            WHERE st.id=$step AND st.run_id=$run
            """, r => new
        {
            Result = r.IsNull(0) ? null : r.Text(0),
            Hash = r.IsNull(1) ? null : r.Text(1),
            Generation = r.IsNull(2) ? (int?)null : (int)r.Long(2),
            Machine = r.IsNull(3) ? null : r.Text(3),
            Step = r.Long(4) != 0,
            Spec = r.Long(5) != 0
        }, new SqlArg("$step", observed.StepId), new("$run", observed.RunId),
            new("$attempt", observed.Attempt), new("$session", observed.SessionId), new("$epoch", observed.Epoch));
        if (original is null) throw new ArgumentException("The original workflow step changed.", nameof(observed));
        if (original.Result is null) return null; // Ordinary Board steps keep their existing lifecycle.
        if (original.Hash is null || !original.Step || !original.Spec || original.Generation is null || original.Machine is null)
            throw new ArgumentException("The original workflow execution binding changed.", nameof(observed));
        var body = new WorkflowReleaseBody(observed, source.ChatRunId, source.TerminalSeq, source.TerminalState, source.EndedAt,
            original.Generation.Value, original.Machine, original.Hash);
        return new(body, JsonSerializer.Serialize(body, WorkflowReleaseJson.Default.WorkflowReleaseBody));
    }

    public CommandResult ApplyWorkflowRelease(IWriteScope scope, PreparedWorkflowRelease prepared, long nowMs)
    {
        var body = prepared.Body; var observed = body.Observed;
        // One command per actual execution, independent of current step version or repeated release callbacks.
        var command = DispatchIdentity.Id("workflow-execution-released/v1", _machine, observed.SessionId, observed.StepId,
            observed.Attempt!.Value.ToString(CultureInfo.InvariantCulture), observed.Epoch.ToString(CultureInfo.InvariantCulture), body.ChatRunId);
        var context = new CommandContext(command, _machine, new(ActorKind.System, "adapter:workflow-release"), observed.StepId!, 0, observed.Epoch, nowMs);
        return EngineJournal.Execute(scope, context, prepared.Canonical,
            () => ReceiptTransitions.Released(scope, context, prepared, _expiryPolicy));
    }
}
