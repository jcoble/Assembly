using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Runs;

public static class StepTransitions
{
    private sealed record Step(long Version, string RunId, string State, int Attempt, int UsedAttempts, long MaxAttempts,
        bool RuleSkippable, string? SessionId, string OwnerId, string RunState, long OwnerEpoch, string? Proof, string? Reason);
    private sealed record Worker(string? ParentId, string? RunId, string State, long Epoch);

    public static CommandResult Apply(IWriteScope s, CommandContext c, StepCommand command) => ApplyCore(s, c, command, null);

    private static CommandResult ApplyCore(IWriteScope s, CommandContext c, StepCommand command, string? checkedReceiptId)
    {
        var step = Find(s, c.EntityId);
        if (step is null) return Refused(c, "bad_args");
        if (command is StartStep start) return Start(s, c, step, start);
        if (command is not SetStepState fact) return Refused(c, "unsupported_command", step);
        if (checkedReceiptId is null && fact.State == "succeeded" && s.QueryFirst("SELECT result IS NOT NULL FROM engine_step WHERE id=$id", r => r.Long(0), new SqlArg("$id", c.EntityId)) != 0)
            return Refused(c, "receipt_required", step);
        var worker = step.SessionId is null ? null : FindWorker(s, step.SessionId);
        var epoch = worker?.Epoch ?? step.OwnerEpoch;
        if (c.Epoch != epoch) return new(false, false, "stale_epoch", step.Version, 0, epoch);
        if (c.Actor.Kind != ActorKind.System && (c.Actor.Kind != ActorKind.Session || c.Actor.Id != step.SessionId))
            return Refused(c, "forbidden", step);
        if (worker is not null && (worker.ParentId != step.OwnerId || worker.RunId != step.RunId))
            return Refused(c, "forbidden", step);
        if (fact.Attempt != step.Attempt) return Refused(c, "wrong_attempt", step);
        var error = ValidateFact(fact, step, c);
        if (error is not null) return Refused(c, error, step);
        var finished = RunTransitions.IsTerminal(step.RunState) || step.State is "succeeded" or "skipped" or "cancelled" ||
            step.State == "failed" && step.UsedAttempts >= step.MaxAttempts;
        // Only authenticated adapter facts bypass a stale version after completion.
        if (finished && c.Actor.Kind == ActorKind.System)
            return RecordLate(s, c, step, fact, epoch);
        if (c.ExpectedVersion != step.Version) return Refused(c, "version_conflict", step);
        if (c.Actor.Kind == ActorKind.Session && worker?.State is "ended" or "lost" or "handing_off")
            return Refused(c, "forbidden", step);
        if (c.Actor.Kind == ActorKind.Session && fact.State == step.State && fact.Reason == step.Reason &&
            step.Proof is not null && fact.ProofUrls.SequenceEqual(
                JsonSerializer.Deserialize(step.Proof, EngineJson.Default.IReadOnlyListString)!))
            return new(true, false, null, step.Version, 0, epoch);
        if (finished) return Refused(c, "invalid_transition", step);
        if (step.RunState == "cancelling") return Refused(c, "run_cancelling", step);
        var allowed = (step.State, fact.State) is ("running", "waiting" or "succeeded" or "failed" or "cancelled")
            or ("waiting", "running" or "succeeded" or "failed" or "cancelled") or ("pending", "skipped");
        if (!allowed) return Refused(c, "invalid_transition", step);
        var ended = fact.State is "succeeded" or "failed" or "skipped" or "cancelled";
        var proof = JsonSerializer.Serialize(fact.ProofUrls, EngineJson.Default.IReadOnlyListString);
        s.Execute("""
            UPDATE engine_step SET state=$state,version=version+1,updated_ms=$now,ended_ms=$ended,
              proof_json=$proof,outcome_reason=$reason WHERE id=$id
            """, new("$state", fact.State), new("$now", c.NowMs), new("$ended", ended ? c.NowMs : null),
            new("$proof", proof), new("$reason", fact.Reason), new("$id", c.EntityId));
        UpdateAttempt(s, c, fact, false, proof, ended);
        List<EventDraft> events = [Changed(c.EntityId, step, step.Version + 1, fact.State, epoch)];
        events.AddRange(RunTransitions.Recompute(s, c, step.RunId));
        return EngineJournal.Commit(s, c, step.Version + 1, epoch, events, []);
    }

    internal static CommandResult CompleteCheckedReceipt(IWriteScope s, CommandContext c, string receiptId)
    {
        var receipt = s.QueryFirst("""
            SELECT receipt.attempt,json_extract(receipt.body_json,'$.report.proofUrls'),json_extract(receipt.body_json,'$.report.summary')
            FROM engine_workflow_receipt receipt JOIN engine_step st ON st.id=receipt.step_id
            JOIN engine_run run ON run.id=st.run_id JOIN engine_session worker ON worker.id=st.session_id
            JOIN engine_step_attempt attempt ON attempt.step_id=st.id AND attempt.attempt=st.attempt
            WHERE receipt.id=$receipt AND receipt.state='accepted' AND receipt.step_id=$step
              AND receipt.run_id=run.id AND receipt.machine=$machine AND run.machine=$machine
              AND receipt.generation=st.generation AND receipt.attempt=st.attempt
              AND receipt.session_id=st.session_id AND receipt.session_id=st.authority_session_id
              AND receipt.epoch=worker.epoch AND receipt.epoch=st.authority_epoch AND receipt.epoch=$epoch
              AND worker.machine=$machine AND worker.holder='local' AND st.authority_machine=$machine AND attempt.authority_machine=$machine AND attempt.execution_epoch=$epoch
              AND attempt.generation=st.generation AND attempt.receipt_id=receipt.id AND st.receipt_id=receipt.id
            """, r => (Attempt: (int)r.Long(0), Proof: r.Text(1), Summary: r.IsNull(2) ? null : r.Text(2)), new SqlArg("$receipt", receiptId),
            new("$step", c.EntityId), new("$machine", c.Machine), new("$epoch", c.Epoch));
        if (c.Actor.Kind != ActorKind.System || receipt.Proof is null) return Refused(c, "receipt_required");
        var proof = JsonSerializer.Deserialize(receipt.Proof, EngineJson.Default.IReadOnlyListString)!;
        return ApplyCore(s, c, new SetStepState("succeeded", receipt.Attempt, proof, receipt.Summary, false), receiptId);
    }

    private static CommandResult Start(IWriteScope s, CommandContext c, Step step, StartStep command)
    {
        if (step.RunState == "cancelling") return Refused(c, "run_cancelling", step);
        var worker = FindWorker(s, command.SessionId);
        if (worker is null) return Refused(c, "bad_args", step);
        if (!RunTransitions.CanControl(c, step.OwnerId) || worker.ParentId != step.OwnerId || worker.RunId != step.RunId)
            return Refused(c, "forbidden", step);
        if (c.Epoch != worker.Epoch) return new(false, false, "stale_epoch", step.Version, 0, worker.Epoch);
        if (c.ExpectedVersion != step.Version) return Refused(c, "version_conflict", step);
        if (RunTransitions.IsTerminal(step.RunState) || worker.State is "ended" or "lost" ||
            step.State != "pending" && !(step.State == "failed" && step.UsedAttempts < step.MaxAttempts))
            return Refused(c, "invalid_transition", step);
        if (s.QueryFirst("""
            SELECT COUNT(*) FROM engine_step_dependency d JOIN engine_step dep ON dep.id=d.dependency_id
            WHERE d.step_id=$id AND NOT (
                dep.state='succeeded'
                OR (dep.state='skipped' AND dep.rule_skippable=1 AND dep.outcome_reason IS NOT NULL)
                OR (d.optional=1 AND dep.state IN ('failed','cancelled')))
            """, r => r.Long(0), new SqlArg("$id", c.EntityId)) > 0)
            return Refused(c, "dependency_not_ready", step);
        var attempt = step.Attempt + 1;
        s.Execute("""
            UPDATE engine_step SET state='running',version=version+1,updated_ms=$now,session_id=$session,
                attempt=$attempt,attempt_in_generation=attempt_in_generation+1,reminder_sent=0,report_requested=0,started_ms=$now,ended_ms=NULL,proof_json=NULL,outcome_reason=NULL WHERE id=$id
            """, new("$now", c.NowMs), new("$session", command.SessionId), new("$attempt", attempt), new("$id", c.EntityId));
        s.Execute("INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,attempt_in_generation) SELECT id,$attempt,'running',$now,0,generation,attempt_in_generation FROM engine_step WHERE id=$id",
            new("$id", c.EntityId), new("$attempt", attempt), new("$now", c.NowMs));
        List<EventDraft> events = [Changed(c.EntityId, step with { SessionId = command.SessionId }, step.Version + 1, "running", worker.Epoch)];
        events.AddRange(RunTransitions.Recompute(s, c, step.RunId));
        return EngineJournal.Commit(s, c, step.Version + 1, worker.Epoch, events, []);
    }

    private static string? ValidateFact(SetStepState fact, Step step, CommandContext c)
    {
        if (fact.State is not ("running" or "waiting" or "succeeded" or "failed" or "skipped" or "cancelled") || fact.ProofUrls is null)
            return "bad_args";
        if (fact.State == "succeeded" && !fact.ProofUrls.Any(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"))
            return "proof_required";
        if (fact.State is "failed" or "skipped" && string.IsNullOrWhiteSpace(fact.Reason)) return "reason_required";
        if (fact.State == "skipped" && (!step.RuleSkippable || !fact.ByWorkflowRule || c.Actor.Kind != ActorKind.System))
            return "forbidden";
        return null;
    }

    private static CommandResult RecordLate(IWriteScope s, CommandContext c, Step step, SetStepState fact, long epoch)
    {
        s.Execute("""
            INSERT INTO engine_step_fact(id,step_id,attempt,kind,at_ms,data_json,late)
            VALUES($id,$step,$attempt,$kind,$now,$data,1)
            """, new("$id", c.CommandId.ToString("N")), new("$step", c.EntityId), new("$attempt", fact.Attempt),
            new("$kind", fact.State), new("$now", c.NowMs), new("$data", JsonSerializer.Serialize(fact, EngineJson.Default.SetStepState)));
        if (s.QueryFirst("SELECT run.definition_hash IS NOT NULL FROM engine_step st JOIN engine_run run ON run.id=st.run_id WHERE st.id=$id",
            r => r.Long(0), new SqlArg("$id", c.EntityId)) == 0)
            UpdateAttempt(s, c, fact, true, JsonSerializer.Serialize(fact.ProofUrls, EngineJson.Default.IReadOnlyListString),
                fact.State is "succeeded" or "failed" or "skipped" or "cancelled");
        var late = Changed(c.EntityId, step, step.Version, step.State, epoch) with { Kind = "step.late", Late = true };
        return EngineJournal.Commit(s, c, step.Version, epoch, [late], [], late: true);
    }

    private static void UpdateAttempt(IWriteScope s, CommandContext c, SetStepState fact, bool late, string proof, bool ended) =>
        s.Execute("""
            UPDATE engine_step_attempt SET state=$state,ended_ms=$ended,proof_json=$proof,reason=$reason,late=$late
            WHERE step_id=$id AND attempt=$attempt
            """, new("$state", fact.State), new("$ended", ended ? c.NowMs : null), new("$proof", proof),
            new("$reason", fact.Reason), new("$late", late ? 1 : 0), new("$id", c.EntityId), new("$attempt", fact.Attempt));

    private static Step? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT st.version,st.run_id,st.state,st.attempt,st.max_attempts,st.rule_skippable,st.session_id,
            r.owner_session_id,r.state,se.epoch,st.proof_json,st.outcome_reason,CASE WHEN r.definition_hash IS NULL THEN st.attempt ELSE st.attempt_in_generation END FROM engine_step st JOIN engine_run r ON r.id=st.run_id
            JOIN engine_session se ON se.id=r.owner_session_id WHERE st.id=$id
        """, r => new Step(r.Long(0), r.Text(1), r.Text(2), (int)r.Long(3), (int)r.Long(12), r.Long(4), r.Long(5) != 0,
            r.IsNull(6) ? null : r.Text(6), r.Text(7), r.Text(8), r.Long(9), r.IsNull(10) ? null : r.Text(10), r.IsNull(11) ? null : r.Text(11)), new SqlArg("$id", id));
    private static Worker? FindWorker(IWriteScope s, string id) => s.QueryFirst("""
        SELECT parent_id,run_id,state,epoch FROM engine_session WHERE id=$id
        """, r => new Worker(r.IsNull(0) ? null : r.Text(0), r.IsNull(1) ? null : r.Text(1), r.Text(2), r.Long(3)), new SqlArg("$id", id));
    private static EventDraft Changed(string id, Step step, long version, string state, long epoch) =>
        new("step.changed", "step", id, version, false, new(step.SessionId, step.RunId, id, step.OwnerId, state, epoch));
    private static CommandResult Refused(CommandContext c, string error, Step? step = null) =>
        new(false, false, error, step?.Version ?? 0, 0, c.Epoch);
}
