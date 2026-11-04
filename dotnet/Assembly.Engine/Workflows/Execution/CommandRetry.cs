using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Execution;

internal sealed record CommandRetryBody(CommandContext Context, string EffectId, long EffectVersion,
    long RunVersion, long OwnerVersion, CommandExecutionIntent Intent, CommandProcessIdentity Process, string PayloadSha256);

/// <summary>Only the owning host can construct this after positively observing original executor absence.</summary>
public sealed class PreparedWorkflowCommandRetry
{
    internal CommandRetryBody Body { get; }
    public CommandContext Context { get; }
    internal PreparedWorkflowCommandRetry(CommandRetryBody body)
    {
        var identity = JsonSerializer.Serialize(body with { Context = body.Context with { CommandId = Guid.Empty, NowMs = 0 } },
            CommandRetryJson.Default.CommandRetryBody);
        Context = body.Context with { CommandId = new Guid(Convert.FromHexString(CommandStepTransitions.Digest("workflow-command-retry/v1\n" + identity))[..16]) };
        var json = JsonSerializer.Serialize(body with { Context = Context with { NowMs = 0 } }, CommandRetryJson.Default.CommandRetryBody);
        Body = JsonSerializer.Deserialize(json, CommandRetryJson.Default.CommandRetryBody)!;
    }
}

public static class CommandRetryTransitions
{
    internal static CommandResult Apply(IWriteScope s, CommandContext c, PreparedWorkflowCommandRetry prepared)
    {
        var b = prepared.Body; var i = b.Intent;
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        if (b.Context != c with { NowMs = 0 } || !i.Repeat || b.Process.Pid <= 0 || b.Process.StartUtcTicks <= 0 || b.Process.GroupId != b.Process.Pid)
            return Refuse("forbidden");
        var row = s.QueryFirst("""
            SELECT n.version,n.state,n.execution_json,n.session_id,n.addressee_session_id,owner.version,owner.epoch,
              r.version,o.version,o.state,o.payload_json,o.executor_json,st.version
            FROM engine_needs_me n JOIN engine_run r ON r.id=n.run_id JOIN engine_session owner ON owner.id=r.owner_session_id
            JOIN engine_step st ON st.id=n.step_id JOIN engine_outbox o ON o.id=$effect
            WHERE n.id=$need AND n.source_kind='workflow' AND r.machine=$machine AND n.session_id=r.owner_session_id
              AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
              AND owner.holder='local' AND owner.machine=$machine AND owner.reserved_epoch IS NULL AND owner.parent_id IS NULL
              AND owner.state NOT IN ('foreign','ended','handing_off','lost') AND o.kind='workflow_command' AND o.session_id IS NULL
            """, r => new
        {
            NeedVersion = r.Long(0),
            NeedState = r.Text(1),
            Binding = r.Text(2),
            Owner = r.Text(3),
            Addressee = r.IsNull(4) ? null : r.Text(4),
            OwnerVersion = r.Long(5),
            Epoch = r.Long(6),
            RunVersion = r.Long(7),
            EffectVersion = r.Long(8),
            EffectState = r.Text(9),
            Payload = r.Text(10),
            Executor = r.IsNull(11) ? null : r.Text(11),
            StepVersion = r.Long(12)
        },
            new SqlArg("$effect", b.EffectId), new("$need", c.EntityId), new("$machine", c.Machine));
        if (row is null) return Refuse("forbidden");
        if (row.NeedState != "open") return Refuse("already_resolved");
        if (!(c.Actor is { Kind: ActorKind.Window, Id: "owner" } || c.Actor.Kind == ActorKind.Session && c.Actor.Id == row.Owner && row.Addressee == row.Owner))
            return Refuse("forbidden");
        if (row.NeedVersion != c.ExpectedVersion || row.OwnerVersion != b.OwnerVersion || row.RunVersion != b.RunVersion || row.EffectVersion != b.EffectVersion)
            return Refuse("version_conflict");
        if (row.Epoch != c.Epoch) return Refuse("stale_epoch");
        var binding = JsonSerializer.Deserialize(row.Binding, WorkflowNeedJson.Default.WorkflowExecutionNeed)!;
        if (binding.Reason != "command_uncertain" || binding.EffectId != b.EffectId || binding.OrderKey != i.OrderKey || binding.StepId != i.StepId ||
            binding.Generation != i.Generation || binding.Attempt != i.Attempt || binding.CommandIndex != i.CommandIndex || binding.ExecutionEpoch != i.ExecutionEpoch ||
            !CommandStepTransitions.Current(s, i, c.Machine)) return Refuse("stale_attempt");
        if (row.EffectState != "unavailable" || CommandStepTransitions.Digest(row.Payload) != b.PayloadSha256 || row.Executor is null ||
            JsonSerializer.Deserialize(row.Executor, WorkflowExecutionJson.Default.CommandProcessIdentity) != b.Process || s.QueryFirst("""
                SELECT COUNT(*) FROM engine_outbox WHERE order_key=$key AND id!=$effect AND state NOT IN ('done','failed','dead')
                """, r => r.Long(0), new SqlArg("$key", i.OrderKey), new("$effect", b.EffectId)) != 0) return Refuse("executor_busy");
        s.Execute("""
            UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answer_json='"retry"',answered_by=$actor,answered_ms=$now WHERE id=$need;
            UPDATE engine_outbox SET state='failed',version=version+1,updated_ms=$now,next_ms=NULL,last_error='explicit_repeat_recovery' WHERE id=$effect;
            UPDATE engine_step_attempt SET state='failed',ended_ms=$now,reason='interrupted',gate_state='settled' WHERE step_id=$step AND attempt=$attempt;
            UPDATE engine_step SET state='pending',dispatch_state='pending',outcome_reason=NULL,ended_ms=NULL,deadline_ms=NULL,
              generation=generation+1,attempt_in_generation=0,scope_granted=0,version=version+1,updated_ms=$now WHERE id=$step;
            """, new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$need", c.EntityId), new("$effect", b.EffectId), new("$step", i.StepId), new("$attempt", i.Attempt));
        CommandStepTransitions.History(s, c, i, "command_retry_authorized", "{}");
        List<EventDraft> events = [new("needs_me.answered", "need", c.EntityId, row.NeedVersion + 1, false, new(row.Owner, i.RunId, i.StepId, row.Owner, "answered", c.Epoch)),
            new("effect.changed", "effect", b.EffectId, row.EffectVersion + 1, false, new(null, i.RunId, i.StepId, null, "failed", i.ExecutionEpoch)),
            new("step.changed", "step", i.StepId, row.StepVersion + 1, false, new(null, i.RunId, i.StepId, null, "pending", i.ExecutionEpoch))];
        events.AddRange(RunTransitions.Recompute(s, c, i.RunId));
        return EngineJournal.Commit(s, c, row.NeedVersion + 1, c.Epoch, events, []);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandRetryBody))]
internal sealed partial class CommandRetryJson : JsonSerializerContext;
