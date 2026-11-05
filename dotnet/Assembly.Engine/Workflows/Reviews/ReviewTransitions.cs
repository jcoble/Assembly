using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Workflows.Reviews;

public sealed record ReviewFinished(string ReceiptId) : WorkflowCommand;
public sealed record RedoStep(string TargetStepId, IReadOnlyList<string> FindingReceiptIds, string? OutputReceiptId) : WorkflowCommand;
public sealed record WorkflowExecutionFence(string StepId, int Generation, int Attempt, long ExecutionEpoch);
internal sealed record RedoRequest(string TargetStepId, int Generation, string SourceStepId, string ReceiptId,
    IReadOnlyList<string> PeerStepIds, string SourceKind = "review", string? OutputReceiptId = null);
internal sealed record RedoApplied(string TargetStepId, int Generation, IReadOnlyList<string> FindingReceiptIds, string? OutputReceiptId = null);

public static partial class ReviewTransitions
{
    /// <summary>ReceiptChecks calls this in the same writer after accepting the original review receipt.</summary>
    public static CommandResult Finished(IWriteScope s, CommandContext c, string receiptId)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        var row = s.QueryFirst("""
            SELECT st.run_id,st.definition_key,st.version,st.generation,st.attempt,receipt.body_json,snap.snapshot_json
            FROM engine_step st JOIN engine_workflow_receipt receipt ON receipt.step_id=st.id
            JOIN engine_workflow_snapshot snap ON snap.run_id=st.run_id JOIN engine_run r ON r.id=st.run_id
            WHERE st.id=$step AND receipt.id=$receipt AND receipt.state='accepted' AND receipt.kind='review'
              AND receipt.generation=st.generation AND receipt.attempt=st.attempt AND r.machine=$machine
              AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
            """, r => new
        {
            Run = r.Text(0),
            Key = r.Text(1),
            Version = r.Long(2),
            Generation = checked((int)r.Long(3)),
            Attempt = checked((int)r.Long(4)),
            Body = r.Text(5),
            Snapshot = r.Text(6)
        },
            new SqlArg("$step", c.EntityId), new("$receipt", receiptId), new("$machine", c.Machine));
        if (row is null) return Refuse("stale_attempt");
        if (row.Version != c.ExpectedVersion) return Refuse("version_conflict");
        using var body = JsonDocument.Parse(row.Body);
        var review = body.RootElement.GetProperty("report").GetProperty("receipt").Deserialize(ReceiptJson.Default.ReviewReceipt)!;
        if (review.Verdict == "pass" && review.Findings.Count == 0) return Refuse("review_has_no_findings");
        var start = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var spec = start.Snapshot.Definition.Steps[row.Key];
        // review.of and on_fail retain distinct triggers. This callback handles review.of only.
        if (spec.Kind != "review" || spec.RedoTarget is null) return Refuse("review_target_missing");
        var target = s.QueryFirst("SELECT id FROM engine_step WHERE run_id=$run AND definition_key=$key", r => r.Text(0),
            new SqlArg("$run", row.Run), new("$key", spec.RedoTarget))!;
        var peers = start.Snapshot.Definition.Steps.Values.Where(step => step.Kind == "review" && step.RedoTarget == spec.RedoTarget)
            .Select(step => start.StepIds[step.Id]).Order(StringComparer.Ordinal).ToArray();
        return Request(s, c, row.Run, row.Attempt, row.Generation, row.Version, target, receiptId, peers, "review");
    }

    private static CommandResult Request(IWriteScope s, CommandContext c, string run, int attempt, int generation,
        long sourceVersion, string target, string receiptId, IReadOnlyList<string> peers, string sourceKind, string? outputReceiptId = null)
    {
        if (s.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_history WHERE run_id=$run AND kind='redo_requested'
              AND json_extract(safe_body_json,'$.receiptId')=$receipt
            """, r => r.Long(0), new SqlArg("$run", run), new("$receipt", receiptId)) != 0)
            return new(true, false, null, sourceVersion, 0, c.Epoch);
        var request = new RedoRequest(target, generation, c.EntityId, receiptId, peers, sourceKind, outputReceiptId);
        History(s, c, run, c.EntityId, attempt, generation, "redo_requested",
            JsonSerializer.Serialize(request, ReviewTransitionJson.Default.RedoRequest));
        var peerJson = JsonSerializer.Serialize((IReadOnlyList<string>)peers, EngineJson.Default.IReadOnlyListString);
        s.Execute("""
            WITH RECURSIVE affected(id) AS (
                SELECT $target UNION SELECT d.step_id FROM engine_step_dependency d JOIN affected a ON d.dependency_id=a.id
            ) UPDATE engine_step SET dispatch_state='redo_pending',version=version+1,updated_ms=$now
              WHERE run_id=$run AND id IN affected AND id NOT IN (SELECT value FROM json_each($peers)) AND dispatch_state IS NOT 'redo_pending';
            UPDATE engine_step_attempt SET cancel_requested=1 WHERE EXISTS(SELECT 1 FROM engine_step st
              WHERE st.id=step_id AND st.attempt=engine_step_attempt.attempt AND st.run_id=$run AND st.dispatch_state='redo_pending');
            UPDATE engine_step SET state='waiting',receipt_id=CASE WHEN $sourceKind='review' THEN $receipt ELSE NULL END,outcome_reason='review_findings',dispatch_state='review_settled',
              version=version+1,updated_ms=$now WHERE id=$source;
            UPDATE engine_step_attempt SET state='failed',ended_ms=$now,reason=$sourceReason
              WHERE step_id=$source AND attempt=$attempt AND state IN ('running','waiting');
            """, new("$target", target), new("$run", run), new("$peers", peerJson), new("$now", c.NowMs), new("$receipt", receiptId), new("$source", c.EntityId), new("$sourceKind", sourceKind), new("$sourceReason", sourceKind == "command" ? "command_findings" : "review_findings"), new("$attempt", attempt));
        var stops = s.Query("""
            SELECT se.id,se.version,se.epoch,se.parent_id FROM engine_session se JOIN engine_step st ON st.session_id=se.id
            WHERE st.run_id=$run AND st.dispatch_state='redo_pending' AND se.machine=$machine AND se.holder='local'
              AND se.state NOT IN ('ended','foreign') AND se.cancel_requested=0
              AND NOT EXISTS(SELECT 1 FROM engine_outbox o WHERE o.session_id=se.id AND o.kind='stop' AND o.state NOT IN ('done','failed','dead'))
            """, r => (Id: r.Text(0), Version: r.Long(1), Epoch: r.Long(2), Parent: r.IsNull(3) ? null : r.Text(3)),
            new SqlArg("$run", run), new("$machine", c.Machine));
        List<EffectDraft> effects = [];
        List<EventDraft> events = [];
        foreach (var stop in stops)
        {
            s.Execute("UPDATE engine_session SET cancel_requested=1,version=version+1,updated_ms=$now WHERE id=$id",
                new("$now", c.NowMs), new("$id", stop.Id));
            events.Add(new("session.cancel_requested", "session", stop.Id, stop.Version + 1, false,
                new(stop.Id, run, null, stop.Parent, null, stop.Epoch)));
            effects.Add(new("", "stop", stop.Id, run, JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession)));
        }
        var version = s.QueryFirst("SELECT version FROM engine_step WHERE id=$step", r => r.Long(0), new SqlArg("$step", c.EntityId));
        events.Add(new("workflow.redo_requested", "step", c.EntityId, version, false,
            new(null, run, c.EntityId, null, "waiting", c.Epoch)));
        return EngineJournal.Commit(s, c, version, c.Epoch, events, effects);
    }

    /// <summary>No default release assumption: the host's actual execution producer must prove every original tuple.</summary>
    public static CommandResult Advance(IWriteScope s, CommandContext c,
        Func<IReadScope, WorkflowExecutionFence, bool> released)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System) return Refuse("forbidden");
        var prepared = PrepareAdvance(s, c, released, out var error);
        return prepared is null ? Refuse(error!) : ApplyAdvance(s, c, prepared);
    }

    internal static CommandResult ApplyAdvance(IWriteScope s, CommandContext c, RedoAdvancePreparation prepared)
    {
        var target = prepared.Target; var requests = prepared.Requests; var affected = prepared.Affected;
        var receipts = ReceiptIds(s, requests);
        var current = Findings(s, receipts);
        var outputReceipt = requests.LastOrDefault(request => request.OutputReceiptId is not null)?.OutputReceiptId;
        var previousJson = s.QueryFirst("""
            SELECT safe_body_json FROM engine_workflow_history WHERE run_id=$run AND kind='redo_applied'
              AND json_extract(safe_body_json,'$.targetStepId')=$target ORDER BY history_seq DESC LIMIT 1
            """, r => r.Text(0), new SqlArg("$run", target.Run), new("$target", c.EntityId));
        var previous = previousJson is null ? [] : Findings(s, JsonSerializer.Deserialize(previousJson, ReviewTransitionJson.Default.RedoApplied)!.FindingReceiptIds);
        var allowance = AdditionalRoundAllowance(s, target, c.EntityId, receipts, outputReceipt);
        // The round limit stops a loop nobody is watching. An owner asking for another round is the one it would stop for,
        // and it doesn't spend the rounds a reviewer has.
        var ownerAsked = requests.Any(request => request.SourceKind == GateRevisionSource);
        if (!ownerAsked && allowance.Grant is null && ReviewRounds.ShouldPause(previous, current, target.Round, allowance.Max))
        {
            s.Execute("UPDATE engine_run SET paused=1,version=version+1,updated_ms=$now WHERE id=$run", new("$now", c.NowMs), new("$run", target.Run));
            History(s, c, target.Run, c.EntityId, null, target.Generation, "redo_stopped",
                JsonSerializer.Serialize(new RedoApplied(c.EntityId, target.Generation, receipts, outputReceipt), ReviewTransitionJson.Default.RedoApplied));
            return EngineJournal.Commit(s, c, target.Version, c.Epoch,
                [new("workflow.redo_stopped", "run", target.Run, 0, false, new(null, target.Run, c.EntityId, null, "paused", c.Epoch))], []);
        }
        if (allowance.Grant is { } granted)
            History(s, c, target.Run, c.EntityId, null, target.Generation, "redo_round_consumed",
                JsonSerializer.Serialize(new AdditionalRoundConsumption(c.EntityId, target.Generation, granted.Id, granted.Body.StoppedId),
                    AdditionalRoundJson.Default.AdditionalRoundConsumption));
        foreach (var row in affected)
        {
            s.Execute("""
                UPDATE engine_step SET generation=generation+1,round=round+$rounds,attempt_in_generation=0,state=CASE WHEN state='skipped' THEN 'skipped' ELSE 'pending' END,
                  dispatch_state=CASE WHEN state='skipped' THEN 'settled' ELSE 'pending' END,
                  session_id=CASE WHEN kind='review' THEN NULL ELSE session_id END,receipt_id=NULL,outcome_reason=NULL,
                  ended_ms=NULL,version=version+1,updated_ms=$now WHERE id=$step;
                """, new("$now", c.NowMs), new("$step", row.StepId), new("$rounds", ownerAsked ? 0 : 1));
        }
        History(s, c, target.Run, c.EntityId, null, target.Generation, "redo_applied",
            JsonSerializer.Serialize(new RedoApplied(c.EntityId, target.Generation, receipts, outputReceipt), ReviewTransitionJson.Default.RedoApplied));
        return EngineJournal.Commit(s, c, target.Version + 1, c.Epoch,
            [new("workflow.redo_applied", "step", c.EntityId, target.Version + 1, false,
                new(null, target.Run, c.EntityId, null, "pending", c.Epoch))], []);
    }

    private static IReadOnlyList<string> ReceiptIds(IReadScope s, IReadOnlyList<RedoRequest> requests)
    {
        var roots = JsonSerializer.Serialize((IReadOnlyList<string>)requests.Select(request => request.ReceiptId).ToArray(), EngineJson.Default.IReadOnlyListString);
        return s.Query("""
            WITH roots AS (SELECT DISTINCT step_id,generation,attempt FROM engine_workflow_history
              WHERE id IN (SELECT value FROM json_each($ids)) AND kind='command_ended')
            SELECT h.id FROM engine_workflow_history h JOIN roots r ON r.step_id=h.step_id AND r.generation=h.generation AND r.attempt=h.attempt
            WHERE h.kind='command_ended' AND (json_extract(h.safe_body_json,'$.exitCode')!=0 OR json_extract(h.safe_body_json,'$.error') IS NOT NULL)
            UNION ALL SELECT id FROM engine_workflow_receipt WHERE id IN (SELECT value FROM json_each($ids)) AND state='accepted'
            UNION ALL SELECT id FROM engine_workflow_history WHERE id IN (SELECT value FROM json_each($ids)) AND kind='gate_revised'
            """, r => r.Text(0), new SqlArg("$ids", roots)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> Findings(IReadScope s, IReadOnlyList<string> receiptIds) => s.Query("""
        WITH roots AS (SELECT DISTINCT step_id,generation,attempt FROM engine_workflow_history
          WHERE id IN (SELECT value FROM json_each($ids)) AND kind='command_ended'),
        findings AS (
          SELECT json_extract(f.value,'$.text') text,r.created_ms ordinal1,r.id ordinal2,f.key ordinal3
          FROM engine_workflow_receipt r,json_each(r.body_json,'$.report.receipt.findings') f
          WHERE r.id IN (SELECT value FROM json_each($ids)) AND r.state='accepted'
          UNION ALL
          SELECT json_extract(o.payload_json,'$.script'),h.at_ms,h.id,h.history_seq FROM engine_workflow_history h
          JOIN roots r ON r.step_id=h.step_id AND r.generation=h.generation AND r.attempt=h.attempt
          JOIN engine_outbox o ON o.id=json_extract(h.safe_body_json,'$.effectId')
          WHERE h.kind='command_ended' AND (json_extract(h.safe_body_json,'$.exitCode')!=0 OR json_extract(h.safe_body_json,'$.error') IS NOT NULL)
          UNION ALL
          SELECT json_extract(h.safe_body_json,'$.text'),h.at_ms,h.id,h.history_seq FROM engine_workflow_history h
          WHERE h.id IN (SELECT value FROM json_each($ids)) AND h.kind='gate_revised'
        ) SELECT text FROM findings ORDER BY ordinal1,ordinal2,ordinal3
        """, r => r.Text(0), new SqlArg("$ids", JsonSerializer.Serialize(receiptIds, EngineJson.Default.IReadOnlyListString)));
    private static void History(IWriteScope s, CommandContext c, string run, string step, int? attempt, int generation, string kind, string json) => s.Execute("""
        INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
        SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,$kind,$now,$body
        FROM engine_workflow_history WHERE run_id=$run
        """, new("$id", c.CommandId.ToString("N") + "/" + kind), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", run),
        new("$step", step), new("$attempt", attempt), new("$generation", generation), new("$kind", kind), new("$body", json));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RedoRequest))]
[JsonSerializable(typeof(RedoApplied))]
[JsonSerializable(typeof(IReadOnlyList<WorkflowExecutionFence>))]
internal sealed partial class ReviewTransitionJson : JsonSerializerContext;
