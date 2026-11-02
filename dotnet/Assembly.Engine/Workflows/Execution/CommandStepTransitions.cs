using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Execution;

public static partial class CommandStepTransitions
{
    internal static CommandResult Observe(IWriteScope s, CommandContext c, CommandProcessObserved observed)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System || c.Epoch != observed.ExecutionEpoch || c.EntityId != observed.EffectId || observed.Process.Pid <= 0 ||
            observed.Process.GroupId != observed.Process.Pid || observed.Process.StartUtcTicks <= 0 ||
            !Guid.TryParseExact(observed.Process.Nonce, "N", out _)) return Refuse("forbidden");
        var row = s.QueryFirst("SELECT version,state,payload_json,order_key,executor_json FROM engine_outbox WHERE id=$id AND machine=$machine AND kind='workflow_command'",
            r => new { Version = r.Long(0), State = r.Text(1), Payload = r.Text(2), Key = r.Text(3), Executor = r.IsNull(4) ? null : r.Text(4) },
            new SqlArg("$id", observed.EffectId), new("$machine", c.Machine));
        if (row is null || row.State != "inflight") return Refuse("invalid_transition");
        var i = JsonSerializer.Deserialize(row.Payload, WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        if (row.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (!Matches(observed, i) || row.Key != i.OrderKey) return Refuse("stale_attempt");
        var executor = JsonSerializer.Serialize(observed.Process, WorkflowExecutionJson.Default.CommandProcessIdentity);
        if (row.Executor is not null) return row.Executor == executor ? new(true, false, null, row.Version, 0, c.Epoch) : Refuse("process_identity_conflict");
        // Executor observation does not bump the outbox version held by the worker's eventual CompleteEffect.
        s.Execute("UPDATE engine_outbox SET executor_json=$executor,updated_ms=$now WHERE id=$id",
            new("$executor", executor), new("$now", c.NowMs), new("$id", observed.EffectId));
        return EngineJournal.Commit(s, c, row.Version, c.Epoch,
            [new("effect.process_observed", "effect", observed.EffectId, row.Version, false, new(null, i.RunId, i.StepId, null, "inflight", i.ExecutionEpoch))], []);
    }

    public static bool ValidConfirmation(IReadScope s, Effect effect, EffectReceipt receipt)
    {
        try
        {
            var i = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent);
            var result = receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt);
            if (i is not null && result is { ExecutorAbsent: true })
            {
                var executor = s.QueryFirst("SELECT executor_json FROM engine_outbox WHERE id=$id AND kind='workflow_command'",
                    r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", effect.Id));
                return effect.SessionId is null && effect.RunId == i.RunId && effect.OrderKey == i.OrderKey &&
                    result.EffectId == effect.Id && result.IntentSha256 == Digest(effect.Payload.GetRawText()) &&
                    result.Stopped && result.Process is not null && executor is not null &&
                    JsonSerializer.Deserialize(executor, WorkflowExecutionJson.Default.CommandProcessIdentity) == result.Process &&
                    result.ExitCode is null && result.ExitedPid is null && !result.NotStarted &&
                    result.OutputReceiptPath == "" && result.OutputSha256 == "" && result.OutputBytes == 0 && result.ObservedBytes == 0;
            }
            if (i is not null && result is { NotStarted: true })
                return effect.SessionId is null && effect.RunId == i.RunId && effect.OrderKey == i.OrderKey &&
                    result.EffectId == effect.Id && result.IntentSha256 == Digest(effect.Payload.GetRawText()) &&
                    result.Process is null && result.ExitedPid is null && result.ExitCode is null && result.Stopped &&
                    result.Error == "not_started" && result.OutputReceiptPath == "" && result.OutputSha256 == "" &&
                    result.OutputBytes == 0 && result.ObservedBytes == 0 && s.QueryFirst(
                        "SELECT COUNT(*) FROM engine_outbox WHERE id=$id AND executor_json IS NULL", r => r.Long(0), new SqlArg("$id", effect.Id)) == 1;
            if (i is null || result is null || effect.SessionId is not null || effect.RunId != i.RunId || effect.OrderKey != i.OrderKey ||
                result.EffectId != effect.Id || result.IntentSha256 != Digest(effect.Payload.GetRawText()) ||
                result.OutputReceiptPath != i.OutputPath || !Sha(result.OutputSha256) || result.OutputBytes is < 0 or > 16 * 1024 * 1024 ||
                result.ObservedBytes < result.OutputBytes || Encoding.UTF8.GetByteCount(result.Summary) > 8192 ||
                result.ExitCode is null || result.Process is null && result.ExitedPid is not > 0) return false;
            var saved = s.QueryFirst("SELECT executor_json FROM engine_outbox WHERE id=$id AND kind='workflow_command'",
                r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", effect.Id));
            return result.Process is null ? saved is null && result.ExitedPid > 0
                : saved is not null && JsonSerializer.Deserialize(saved, WorkflowExecutionJson.Default.CommandProcessIdentity) == result.Process;
        }
        catch (JsonException) { return false; }
    }

    public static void Confirm(IWriteScope s, CommandContext c, Effect effect, EffectReceipt receipt)
    {
        var i = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        var result = receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        var current = Current(s, i, c.Machine);
        History(s, c, i, current ? "command_ended" : "command_late", receipt.Data.GetRawText());
        if (!current) return; // Old attempt outcome is immutable; the late observation is a separate fact.
        var stopped = result.Stopped || s.QueryFirst("SELECT cancel_requested FROM engine_step_attempt WHERE step_id=$step AND attempt=$attempt",
            r => r.Long(0) != 0, new SqlArg("$step", i.StepId), new("$attempt", i.Attempt));
        var timedOut = stopped && s.QueryFirst("""
            SELECT a.reason='timed_out' AND r.state NOT IN ('cancelling','cancelled') FROM engine_step_attempt a
            JOIN engine_step st ON st.id=a.step_id JOIN engine_run r ON r.id=st.run_id
            WHERE a.step_id=$step AND a.attempt=$attempt
            """, r => r.Long(0) != 0, new SqlArg("$step", i.StepId), new("$attempt", i.Attempt));
        var last = i.CommandIndex + 1 >= i.CommandCount;
        var changed = result.EndFingerprint is { } end && end != i.Fingerprint || s.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_history WHERE step_id=$step AND attempt=$attempt AND generation=$generation
              AND kind='command_ended' AND json_extract(safe_body_json,'$.endFingerprint') IS NOT NULL
              AND json_extract(safe_body_json,'$.endFingerprint')!=$fingerprint
            """, r => r.Long(0), new SqlArg("$step", i.StepId), new("$attempt", i.Attempt), new("$generation", i.Generation),
            new("$fingerprint", i.Fingerprint)) > 0;
        var failed = result.ExitCode != 0 || result.Error is not null || changed || s.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_history WHERE step_id=$step AND attempt=$attempt AND generation=$generation
              AND kind='command_ended' AND (json_extract(safe_body_json,'$.exitCode')!=0 OR json_extract(safe_body_json,'$.error') IS NOT NULL)
            """, r => r.Long(0), new SqlArg("$step", i.StepId), new("$attempt", i.Attempt), new("$generation", i.Generation)) > 0;
        var redo = last && !stopped && !changed && failed && Reviews.ReviewTransitions.HasCommandTarget(s, i.StepId);
        var state = timedOut ? "waiting" : stopped ? "cancelled" : !last ? "running" : redo ? "waiting" : failed ? "failed" : "succeeded";
        var reason = timedOut ? "timed_out" : stopped ? "cancelled" : changed ? "changed_code" : failed && last ? "command_failed" : null;
        var terminal = stopped || last;
        s.Execute("""
            UPDATE engine_step_attempt SET command_index=$next,gate_state=$gate,state=$state,ended_ms=$ended,reason=$reason
            WHERE step_id=$step AND attempt=$attempt;
            UPDATE engine_step SET state=$state,version=version+1,updated_ms=$now,ended_ms=$ended,outcome_reason=$reason
             ,deadline_ms=CASE WHEN $ended IS NOT NULL THEN NULL ELSE deadline_ms END,
              dispatch_state=CASE WHEN $ended IS NOT NULL THEN 'settled' ELSE dispatch_state END WHERE id=$step;
            """, new("$next", i.CommandIndex + 1), new("$gate", terminal ? "settled" : "ready"), new("$state", state),
            new("$ended", terminal ? c.NowMs : null), new("$reason", reason), new("$step", i.StepId), new("$attempt", i.Attempt), new("$now", c.NowMs));
        var version = s.QueryFirst("SELECT version FROM engine_step WHERE id=$step", r => r.Long(0), new SqlArg("$step", i.StepId));
        List<EventDraft> events = [new("step.changed", "step", i.StepId, version, false, new(null, i.RunId, i.StepId, null, state, i.ExecutionEpoch))];
        if (timedOut) WorkflowExecutionNeeds.OpenTimeout(s, c, effect, i);
        if (redo)
        {
            var requested = Reviews.ReviewTransitions.CommandFailed(s, c with { EntityId = i.StepId, ExpectedVersion = version }, i,
                c.CommandId.ToString("N") + "/command_ended");
            if (!requested.Applied) throw new InvalidOperationException(requested.Error);
        }
        else events.AddRange(RunTransitions.Recompute(s, c, i.RunId));
        EngineJournal.Commit(s, c, version, c.Epoch, events, []);
    }

    internal static bool CanLaunch(IReadScope s, CommandExecutionIntent i, string machine) => Current(s, i, machine) && s.QueryFirst("""
        SELECT COUNT(*) FROM engine_run r JOIN engine_session owner ON owner.id=r.owner_session_id
          JOIN engine_step_attempt a ON a.step_id=$step AND a.attempt=$attempt
        WHERE r.id=$run AND r.machine=$machine AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
          AND a.cancel_requested=0 AND owner.holder='local' AND owner.reserved_epoch IS NULL
          AND owner.state NOT IN ('foreign','ended','handing_off','lost')
        """, r => r.Long(0), new SqlArg("$run", i.RunId), new("$step", i.StepId), new("$attempt", i.Attempt), new("$machine", machine)) == 1;

    internal static bool Current(IReadScope s, CommandExecutionIntent i, string machine) => s.QueryFirst("""
        SELECT COUNT(*) FROM engine_step st JOIN engine_run r ON r.id=st.run_id
        JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
        WHERE st.id=$step AND r.id=$run AND r.machine=$machine AND st.authority_machine=$machine
          AND st.generation=$generation AND st.attempt=$attempt AND a.generation=$generation
          AND a.execution_epoch=$epoch AND a.command_index=$index AND a.gate_state='executing' AND st.dispatch_state IS NOT 'redo_pending'
        """, r => r.Long(0), new SqlArg("$step", i.StepId), new("$run", i.RunId), new("$machine", machine), new("$generation", i.Generation),
        new("$attempt", i.Attempt), new("$epoch", i.ExecutionEpoch), new("$index", i.CommandIndex)) == 1;
    private static bool Matches(CommandProcessObserved o, CommandExecutionIntent i) => o.Generation == i.Generation && o.Attempt == i.Attempt &&
        o.CommandIndex == i.CommandIndex && o.ExecutionEpoch == i.ExecutionEpoch;
    internal static void History(IWriteScope s, CommandContext c, CommandExecutionIntent i, string kind, string json) => s.Execute("""
        INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
        SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,$kind,$now,$body
        FROM engine_workflow_history WHERE run_id=$run
        """, new("$id", c.CommandId.ToString("N") + "/" + kind), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", i.RunId),
        new("$step", i.StepId), new("$attempt", i.Attempt), new("$generation", i.Generation), new("$kind", kind), new("$body", json));
    internal static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool Sha(string text) => text.Length == 64 && text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
