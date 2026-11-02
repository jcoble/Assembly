using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Execution;

public sealed record WorkflowExecutionNeed(string OrderKey, string StepId, int Generation, int Attempt,
    int CommandIndex, long ExecutionEpoch, string EffectId, string Reason);

/// <summary>Command needs use the real run owner as a routing anchor and never enqueue an ACP answer.</summary>
public static class WorkflowExecutionNeeds
{
    public static bool IsWorkflow(IReadScope s, string id) => s.QueryFirst(
        "SELECT COUNT(*) FROM engine_needs_me WHERE id=$id AND source_kind='workflow'", r => r.Long(0), new SqlArg("$id", id)) == 1;

    public static CommandResult Open(IWriteScope s, CommandContext c, Effect effect, string reason)
    {
        CommandResult Refuse(string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
        if (c.Actor.Kind != ActorKind.System || reason is not ("policy" or "command_uncertain")) return Refuse("forbidden");
        var i = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent);
        if (i is null || effect.Kind != "workflow_command" || effect.OrderKey != i.OrderKey || effect.SessionId is not null ||
            !CommandStepTransitions.Current(s, i, c.Machine)) return Refuse("stale_attempt");
        var owner = s.QueryFirst("""
            SELECT r.owner_session_id,se.epoch FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id
            JOIN engine_outbox o ON o.run_id=r.id WHERE r.id=$run AND o.id=$effect AND o.state='inflight'
              AND r.state NOT IN ('succeeded','failed','cancelled','cancelling') AND o.order_key=$key
              AND o.payload_json=$payload AND r.machine=$machine AND se.holder='local' AND se.reserved_epoch IS NULL
              AND se.state NOT IN ('ended','foreign','handing_off','lost')
            """, r => (Id: r.Text(0), Epoch: r.Long(1)), new SqlArg("$run", i.RunId), new("$effect", effect.Id),
            new("$key", i.OrderKey), new("$payload", effect.Payload.GetRawText()), new("$machine", c.Machine));
        if (owner.Id is null) return Refuse("forbidden");
        var id = effect.Id + "/" + reason;
        if (s.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE id=$id", r => r.Long(0), new SqlArg("$id", id)) != 0)
            return new(true, false, null, 1, 0, owner.Epoch);
        var execution = new WorkflowExecutionNeed(i.OrderKey, i.StepId, i.Generation, i.Attempt, i.CommandIndex, i.ExecutionEpoch, effect.Id, reason);
        s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,kind,text,
              options_json,state,addressee_session_id,source_kind,execution_json)
            VALUES($id,1,$machine,$now,$now,$owner,$run,$step,$kind,$text,$options,'open',$owner,'workflow',$execution);
            UPDATE engine_workflow_policy SET need_id=$id WHERE run_id=$run AND step_id=$step AND command_hash=$hash AND decision='ask';
            UPDATE engine_step SET state='waiting',version=version+1,updated_ms=$now,outcome_reason=$reason WHERE id=$step;
            """, new("$id", id), new("$machine", c.Machine), new("$now", c.NowMs), new("$owner", owner.Id), new("$run", i.RunId), new("$step", i.StepId),
            new("$kind", reason == "policy" ? "permission" : "command_uncertain"), new("$text", reason == "policy" ? "Allow this workflow command?" : "Command execution is uncertain; confirm its executor stopped before retrying."),
            new("$options", reason == "policy" ? "[\"allow_once\",\"deny\"]" : i.Repeat ? "[\"retry\",\"cancel\"]" : "[\"cancel\"]"),
            new("$execution", JsonSerializer.Serialize(execution, WorkflowNeedJson.Default.WorkflowExecutionNeed)), new("$hash", CommandStepTransitions.Digest(effect.Payload.GetRawText())), new("$reason", reason));
        List<EventDraft> events = [new("needs_me.changed", "need", id, 1, false, new(owner.Id, i.RunId, i.StepId, owner.Id, "open", owner.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, i.RunId));
        return EngineJournal.Commit(s, c, 1, owner.Epoch, events, []);
    }

    /// <summary>Called only after existing owner/parent authorization; raw ResolveNeed never supplies these booleans.</summary>
    public static CommandResult Resolve(IWriteScope s, CommandContext c, ResolveNeed answer, bool ownerAuthorized, bool parentAuthorized)
    {
        CommandResult Refuse(string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
        var row = s.QueryFirst("""
            SELECT n.version,n.state,n.execution_json,n.session_id,n.run_id,n.step_id,se.epoch,se.state,se.holder,se.reserved_epoch,
              r.state,n.addressee_session_id FROM engine_needs_me n JOIN engine_session se ON se.id=n.session_id
              JOIN engine_run r ON r.id=n.run_id WHERE n.id=$id AND n.source_kind='workflow' AND r.machine=$machine
            """, r => new
        {
            Version = r.Long(0),
            State = r.Text(1),
            Binding = r.Text(2),
            Owner = r.Text(3),
            Run = r.Text(4),
            Step = r.Text(5),
            Epoch = r.Long(6),
            OwnerState = r.Text(7),
            Holder = r.Text(8),
            Reserved = !r.IsNull(9),
            RunState = r.Text(10),
            Addressee = r.IsNull(11) ? null : r.Text(11)
        },
            new SqlArg("$id", c.EntityId), new("$machine", c.Machine));
        if (row is null) return Refuse("bad_args");
        if (row.State != "open") return Refuse("already_resolved");
        if (row.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (row.Epoch != c.Epoch) return Refuse("stale_epoch");
        if (!(ownerAuthorized && c.Actor.Kind == ActorKind.Window || parentAuthorized && c.Actor.Kind == ActorKind.Session && c.Actor.Id == row.Owner && row.Addressee == row.Owner))
            return Refuse("forbidden");
        if (row.RunState is "succeeded" or "failed" or "cancelled" or "cancelling" || row.OwnerState is "ended" or "lost" or "foreign" or "handing_off" || row.Holder != "local" || row.Reserved)
            return Refuse("stale_attempt");
        var binding = JsonSerializer.Deserialize(row.Binding, WorkflowNeedJson.Default.WorkflowExecutionNeed)!;
        var effect = s.QueryFirst("SELECT payload_json,state,executor_json FROM engine_outbox WHERE id=$effect AND kind='workflow_command' AND order_key=$key",
            r => new { Payload = r.Text(0), State = r.Text(1), Executor = r.IsNull(2) ? null : r.Text(2) }, new SqlArg("$effect", binding.EffectId), new("$key", binding.OrderKey));
        if (effect is null || effect.State is not ("inflight" or "unavailable" or "uncertain")) return Refuse("stale_attempt");
        var i = JsonSerializer.Deserialize(effect.Payload, WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        if (i.StepId != binding.StepId || i.Generation != binding.Generation || i.Attempt != binding.Attempt || i.CommandIndex != binding.CommandIndex ||
            i.ExecutionEpoch != binding.ExecutionEpoch || !CommandStepTransitions.Current(s, i, c.Machine)) return Refuse("stale_attempt");
        var choice = answer.Answer is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        if (answer.State != "answered" || choice is null) return Refuse("bad_args");
        if (binding.Reason == "policy")
        {
            if (choice is not ("allow_once" or "deny") || effect.Executor is not null) return Refuse("bad_args");
            s.Execute("UPDATE engine_workflow_policy SET decision=$decision,version=version+1,updated_ms=$now WHERE need_id=$need",
                new("$decision", choice == "allow_once" ? "allow" : "deny"), new("$now", c.NowMs), new("$need", c.EntityId));
            if (choice == "allow_once")
                s.Execute("""
                    UPDATE engine_outbox SET version=version+CASE WHEN state='inflight' THEN 0 ELSE 1 END,updated_ms=$now,
                      state=CASE WHEN state='inflight' THEN state ELSE 'pending' END,next_ms=NULL WHERE id=$effect;
                    UPDATE engine_step SET state='running',outcome_reason=NULL,version=version+1,updated_ms=$now WHERE id=$step;
                    """, new("$effect", binding.EffectId), new("$step", row.Step), new("$now", c.NowMs));
            else Cancel(s, c, i, binding.EffectId, "policy_denied");
        }
        else if (choice == "cancel") Cancel(s, c, i, binding.EffectId, "cancelled");
        else return Refuse(choice == "retry" ? "executor_quiescence_required" : "bad_args");
        s.Execute("""
            UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answer_json=$answer,answered_by=$actor,answered_ms=$now WHERE id=$id
            """, new("$now", c.NowMs), new("$answer", answer.Answer?.GetRawText()), new("$actor", c.Actor.Id), new("$id", c.EntityId));
        List<EventDraft> events = [new("needs_me.answered", "need", c.EntityId, row.Version + 1, false, new(row.Owner, row.Run, row.Step, row.Owner, "answered", row.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, row.Run));
        return EngineJournal.Commit(s, c, row.Version + 1, row.Epoch, events, []);
    }

    public static string Policy(IReadScope s, Effect effect) => s.QueryFirst("""
        SELECT decision FROM engine_workflow_policy WHERE step_id=json_extract($payload,'$.stepId') AND command_hash=$hash
        ORDER BY created_ms DESC,rowid DESC LIMIT 1
        """, r => r.Text(0), new SqlArg("$payload", effect.Payload.GetRawText()), new("$hash", CommandStepTransitions.Digest(effect.Payload.GetRawText()))) ?? "ask";

    private static void Cancel(IWriteScope s, CommandContext c, CommandExecutionIntent i, string effect, string reason) => s.Execute("""
        UPDATE engine_outbox SET version=version+CASE WHEN state='inflight' THEN 0 ELSE 1 END,updated_ms=$now,
          state=CASE WHEN state='inflight' THEN state ELSE 'failed' END,last_error=$reason,next_ms=NULL WHERE id=$effect;
        UPDATE engine_step_attempt SET cancel_requested=1,reason=$reason,
          state=CASE WHEN $reason='policy_denied' THEN 'failed' ELSE state END,
          ended_ms=CASE WHEN $reason='policy_denied' THEN $now ELSE ended_ms END,
          gate_state=CASE WHEN $reason='policy_denied' THEN 'settled' ELSE gate_state END WHERE step_id=$step AND attempt=$attempt;
        UPDATE engine_step SET outcome_reason=$reason,version=version+1,updated_ms=$now,
          state=CASE WHEN $reason='policy_denied' THEN 'failed' ELSE state END,
          ended_ms=CASE WHEN $reason='policy_denied' THEN $now ELSE ended_ms END WHERE id=$step;
        """, new("$reason", reason), new("$effect", effect), new("$step", i.StepId), new("$attempt", i.Attempt), new("$now", c.NowMs));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowExecutionNeed))]
public partial class WorkflowNeedJson : JsonSerializerContext;
