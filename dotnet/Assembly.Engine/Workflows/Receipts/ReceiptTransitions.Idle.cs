using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Workflows.Receipts;

public static partial class ReceiptTransitions
{
    internal static CommandResult Released(IWriteScope scope, CommandContext context, PreparedWorkflowRelease prepared, MessageExpiryPolicy policy)
    {
        var body = prepared.Body; var observed = body.Observed;
        CommandResult Refuse(string error) => new(false, false, error, 0, 0, observed.Epoch);
        // This validates the committed terminal identity, not release. Only the owning host's positive release callback invokes this API.
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$chat AND seq=$seq AND kind='turn'
              AND json_extract(payload,'$.state')=$state AND created_at=$ended
            """, r => r.Long(0), new SqlArg("$session", observed.SessionId), new("$chat", body.ChatRunId),
            new("$seq", body.TerminalSeq), new("$state", body.TerminalState), new("$ended", body.EndedMs)) != 1)
            return Refuse("workflow_terminal_changed");
        var original = scope.QueryFirst("""
            SELECT st.version,st.state,st.attempt,st.generation,st.authority_machine,st.authority_session_id,st.authority_epoch,
              st.dispatch_state,st.result,r.state,r.paused,se.version,se.state,se.epoch,se.machine,se.holder,se.reserved_epoch,se.cancel_requested,
              a.generation,a.authority_machine,COALESCE(a.provider_session_id,CASE WHEN se.epoch=a.execution_epoch THEN se.provider_session_id END)
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id
            JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=$attempt
            JOIN engine_session se ON se.id=a.session_id
            WHERE st.id=$step AND st.run_id=$run AND a.session_id=$session AND a.execution_epoch=$epoch
              AND a.authority_machine=$machine AND a.authority_machine=$authority AND a.generation=$generation AND st.result IS NOT NULL
              AND EXISTS(SELECT 1 FROM engine_workflow_snapshot snapshot WHERE snapshot.run_id=st.run_id AND snapshot.definition_hash=$definition
                AND EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.stepIds') ids WHERE ids.key=st.definition_key AND ids.value=st.id)
                AND EXISTS(SELECT 1 FROM json_each(snapshot.snapshot_json,'$.snapshot.definition.steps') spec
                  WHERE spec.key=st.definition_key AND json_extract(spec.value,'$.result')=st.result))
            """, r => new
        {
            Version = r.Long(0),
            State = r.Text(1),
            Attempt = r.Long(2),
            Generation = r.Long(3),
            AuthorityMachine = r.IsNull(4) ? null : r.Text(4),
            AuthoritySession = r.IsNull(5) ? null : r.Text(5),
            AuthorityEpoch = r.IsNull(6) ? (long?)null : r.Long(6),
            Dispatch = r.IsNull(7) ? null : r.Text(7),
            Kind = r.Text(8),
            RunState = r.Text(9),
            Paused = r.Long(10) != 0,
            SessionVersion = r.Long(11),
            SessionState = r.Text(12),
            Epoch = r.Long(13),
            Machine = r.Text(14),
            Holder = r.Text(15),
            Reserved = !r.IsNull(16),
            Cancel = r.Long(17) != 0,
            OriginalGeneration = (int)r.Long(18),
            OriginalMachine = r.Text(19),
            Native = r.IsNull(20) ? null : r.Text(20)
        }, new SqlArg("$step", observed.StepId), new("$run", observed.RunId), new("$attempt", observed.Attempt),
            new("$session", observed.SessionId), new("$epoch", observed.Epoch), new("$machine", context.Machine),
            new("$authority", body.AuthorityMachine), new("$generation", body.Generation), new("$definition", body.DefinitionHash));
        if (original is null) return Refuse("wrong_attempt");
        var released = new WorkflowExecutionReleased(observed.StepId!, observed.Attempt!.Value, original.OriginalGeneration,
            observed.SessionId, observed.Epoch, original.OriginalMachine, body.ChatRunId, body.TerminalSeq, body.TerminalState, body.EndedMs, original.Native);
        ReleaseHistory(scope, context, observed.RunId!, released, "execution_released", context.CommandId.ToString("N") + "/released",
            JsonSerializer.Serialize(released, WorkflowReleaseJson.Default.WorkflowExecutionReleased));
        var current = original.Attempt == observed.Attempt && original.Generation == original.OriginalGeneration &&
            original.AuthorityMachine == context.Machine && original.AuthoritySession == observed.SessionId && original.AuthorityEpoch == observed.Epoch &&
            original.Epoch == observed.Epoch && original.Machine == context.Machine && original.Holder == "local" && !original.Reserved;
        var active = current && original.State is "running" or "waiting" && original.SessionState == "idle" && !original.Cancel && !original.Paused &&
            original.Dispatch is not ("redo_pending" or "review_settled") && !RunTransitions.IsTerminal(original.RunState) && original.RunState != "cancelling";
        List<EventDraft> events = [new("workflow.execution_released", "step", observed.StepId!, original.Version, !active,
            new(observed.SessionId, observed.RunId, observed.StepId, null, "released", observed.Epoch))];
        if (!active)
        {
            // The release can settle aggregate run state without reviving an ended/lost worker or rewriting its outcome.
            if (current && !RunTransitions.IsTerminal(original.RunState)) events.AddRange(RunTransitions.Recompute(scope, context, observed.RunId!));
            return EngineJournal.Commit(scope, context, original.Version, observed.Epoch, events, [], late: true);
        }
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_receipt WHERE step_id=$step AND attempt=$attempt AND generation=$generation
            """, r => r.Long(0), new SqlArg("$step", observed.StepId), new("$attempt", observed.Attempt), new("$generation", original.OriginalGeneration)) != 0)
            return EngineJournal.Commit(scope, context, original.Version, observed.Epoch, events, []);
        var reminder = scope.QueryFirst("""
            SELECT json_extract(safe_body_json,'$.chatRunId'),json_extract(safe_body_json,'$.terminalSeq')
            FROM engine_workflow_history WHERE run_id=$run AND step_id=$step AND attempt=$attempt AND generation=$generation
              AND kind='report_reminder' AND json_extract(safe_body_json,'$.sessionId')=$session AND json_extract(safe_body_json,'$.epoch')=$epoch
            ORDER BY history_seq LIMIT 1
            """, r => (Chat: r.Text(0), Seq: r.Long(1)), new SqlArg("$run", observed.RunId), new("$step", observed.StepId),
            new("$attempt", observed.Attempt), new("$generation", original.OriginalGeneration), new("$session", observed.SessionId), new("$epoch", observed.Epoch));
        if (reminder.Chat is null)
        {
            var messageId = context.CommandId.ToString("N") + "/report-reminder";
            var note = new WorkflowReportReminder(observed.SessionId, observed.Epoch, body.ChatRunId, body.TerminalSeq, messageId);
            ReleaseHistory(scope, context, observed.RunId!, released, "report_reminder", context.CommandId.ToString("N") + "/reminder",
                JsonSerializer.Serialize(note, WorkflowReleaseJson.Default.WorkflowReportReminder));
            scope.Execute("""
                UPDATE engine_step SET reminder_sent=1,report_requested=1,version=version+1,updated_ms=$now WHERE id=$step;
                UPDATE engine_step_attempt SET reminder_sent=1 WHERE step_id=$step AND attempt=$attempt AND generation=$generation;
                """, new("$step", observed.StepId), new("$attempt", observed.Attempt), new("$generation", original.OriginalGeneration), new("$now", context.NowMs));
            List<EffectDraft> effects = [];
            events.AddRange(MessageTransitions.WorkflowReportReminder(scope, context, messageId, observed.SessionId, original.Kind, effects, policy));
            events.Add(new("step.changed", "step", observed.StepId!, original.Version + 1, false,
                new(observed.SessionId, observed.RunId, observed.StepId, null, original.State, observed.Epoch)));
            return EngineJournal.Commit(scope, context, original.Version + 1, observed.Epoch, events, effects);
        }
        if (reminder.Chat == body.ChatRunId || body.TerminalSeq <= reminder.Seq)
            return EngineJournal.Commit(scope, context, original.Version, observed.Epoch, events, [], late: true);
        ReleaseHistory(scope, context, observed.RunId!, released, "no_report", context.CommandId.ToString("N") + "/no_report",
            JsonSerializer.Serialize(released, WorkflowReleaseJson.Default.WorkflowExecutionReleased));
        var failed = StepTransitions.Apply(scope, context with { ExpectedVersion = original.Version }, new SetStepState("failed", observed.Attempt.Value, [], "no_report", false));
        if (!failed.Applied) throw new InvalidOperationException("Released no-report failure refused: " + failed.Error);
        var ended = SessionTransitions.Apply(scope, context with { EntityId = observed.SessionId, ExpectedVersion = original.SessionVersion },
            new SetSessionState("ended", null, "no_report"), 0, 0, expiryPolicy: policy);
        if (!ended.Applied) throw new InvalidOperationException("Released no-report worker finalization refused: " + ended.Error);
        return EngineJournal.Commit(scope, context, failed.Version, observed.Epoch, events, []);
    }

    private static void ReleaseHistory(IWriteScope scope, CommandContext context, string run, WorkflowExecutionReleased original, string kind, string id, string json) =>
        scope.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,$kind,$now,$body
            FROM engine_workflow_history WHERE run_id=$run
            """, new("$id", id), new("$machine", original.AuthorityMachine), new("$now", context.NowMs), new("$run", run),
            new("$step", original.StepId), new("$attempt", original.Attempt), new("$generation", original.Generation), new("$kind", kind), new("$body", json));
}
