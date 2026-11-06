using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Engine.Workflows.Reviews;

internal sealed record AdditionalRoundGrant(string TargetStepId, int Generation, string StoppedId, string StoppedSha256,
    string GrantedBy, string Actor);
internal sealed record AdditionalRoundConsumption(string TargetStepId, int Generation, string GrantId, string StoppedId);
internal sealed record PendingRoundGrant(string Id, AdditionalRoundGrant Body);

public static partial class ReviewTransitions
{
    /// <summary>Called by the prepared Task10 owner/root control, using the stopped target's current version.</summary>
    internal static CommandResult AllowAdditionalRound(IWriteScope s, CommandContext c)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        var target = s.QueryFirst("""
            SELECT st.run_id,st.version,st.generation,owner.id,owner.epoch,owner.parent_id,r.state
            FROM engine_step st JOIN engine_run r ON r.id=st.run_id JOIN engine_session owner ON owner.id=r.owner_session_id
            WHERE st.id=$target AND st.dispatch_state='redo_pending' AND r.machine=$machine AND r.paused=1
              AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
            """, r => new
        {
            Run = r.Text(0),
            Version = r.Long(1),
            Generation = checked((int)r.Long(2)),
            Owner = r.Text(3),
            Epoch = r.Long(4),
            Parent = r.IsNull(5) ? null : r.Text(5),
            RunState = r.Text(6)
        }, new SqlArg("$target", c.EntityId), new("$machine", c.Machine));
        if (target is null) return Refuse("redo_not_stopped");
        if (!(c.Actor.Kind == ActorKind.Window || c.Actor.Kind == ActorKind.Session && c.Actor.Id == target.Owner && target.Parent is null))
            return Refuse("forbidden");
        if (target.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (target.Epoch != c.Epoch) return Refuse("stale_epoch");
        var stopped = s.QueryFirst("""
            SELECT id,safe_body_json FROM engine_workflow_history WHERE run_id=$run AND step_id=$target
              AND generation=$generation AND kind='redo_stopped' ORDER BY history_seq DESC LIMIT 1
            """, r => (Id: r.Text(0), Body: r.Text(1)), new SqlArg("$run", target.Run), new("$target", c.EntityId), new("$generation", target.Generation));
        if (stopped.Id is null) return Refuse("redo_evidence_missing");
        var source = JsonSerializer.Deserialize(stopped.Body, ReviewTransitionJson.Default.RedoApplied)!;
        if (source.TargetStepId != c.EntityId || source.Generation != target.Generation) return Refuse("redo_evidence_missing");
        if (s.QueryFirst("""
            SELECT EXISTS(SELECT 1 FROM engine_workflow_history WHERE run_id=$run AND step_id=$target AND kind='redo_round_granted'
              AND json_extract(safe_body_json,'$.stoppedId')=$stopped)
            """, r => r.Long(0) != 0, new SqlArg("$run", target.Run), new("$target", c.EntityId), new("$stopped", stopped.Id)))
            return Refuse("additional_round_already_granted");
        var grant = new AdditionalRoundGrant(c.EntityId, target.Generation, stopped.Id, CommandStepTransitions.Digest(stopped.Body),
            c.Actor.Id, c.Actor.Kind.ToString().ToLowerInvariant());
        History(s, c, target.Run, c.EntityId, null, target.Generation, "redo_round_granted",
            JsonSerializer.Serialize(grant, AdditionalRoundJson.Default.AdditionalRoundGrant));
        s.Execute("""
            UPDATE engine_step SET version=version+1,updated_ms=$now WHERE id=$target;
            UPDATE engine_run SET paused=0,version=version+1,updated_ms=$now WHERE id=$run;
            """, new("$now", c.NowMs), new("$target", c.EntityId), new("$run", target.Run));
        return EngineJournal.Commit(s, c, target.Version + 1, c.Epoch,
            [new("workflow.redo_round_granted", "step", c.EntityId, target.Version + 1, false, new(target.Owner, target.Run, c.EntityId, null, "redo_pending", c.Epoch)),
             new("run.changed", "run", target.Run, s.QueryFirst("SELECT version FROM engine_run WHERE id=$run", r => r.Long(0), new SqlArg("$run", target.Run)), false,
                 new(target.Owner, target.Run, null, null, target.RunState, c.Epoch))], []);
    }

    private static (int Max, PendingRoundGrant? Grant) AdditionalRoundAllowance(IReadScope s, RedoAdvanceTarget target,
        string targetStep, IReadOnlyList<string> receipts, string? outputReceipt)
    {
        var consumed = s.QueryFirst("""
            SELECT COUNT(*) FROM engine_workflow_history used JOIN engine_workflow_history granted
              ON granted.id=json_extract(used.safe_body_json,'$.grantId') AND granted.run_id=used.run_id
                AND granted.step_id=used.step_id AND granted.kind='redo_round_granted'
            WHERE used.run_id=$run AND used.step_id=$target AND used.kind='redo_round_consumed'
              AND json_extract(used.safe_body_json,'$.targetStepId')=$target
            """, r => checked((int)r.Long(0)), new SqlArg("$run", target.Run), new("$target", targetStep));
        PendingRoundGrant? grant = null;
        var stopped = s.QueryFirst("""
            SELECT id,safe_body_json FROM engine_workflow_history WHERE run_id=$run AND step_id=$target
              AND generation=$generation AND kind='redo_stopped' ORDER BY history_seq DESC LIMIT 1
            """, r => (Id: r.Text(0), Body: r.Text(1)), new SqlArg("$run", target.Run), new("$target", targetStep), new("$generation", target.Generation));
        if (stopped.Id is not null)
        {
            var source = JsonSerializer.Deserialize(stopped.Body, ReviewTransitionJson.Default.RedoApplied)!;
            if (source.TargetStepId == targetStep && source.Generation == target.Generation &&
                source.FindingReceiptIds.SequenceEqual(receipts) && source.OutputReceiptId == outputReceipt)
                grant = s.QueryFirst("""
                    SELECT granted.id,granted.safe_body_json FROM engine_workflow_history granted
                    WHERE granted.run_id=$run AND granted.step_id=$target AND granted.generation=$generation AND granted.kind='redo_round_granted'
                      AND json_extract(granted.safe_body_json,'$.stoppedId')=$stopped
                      AND json_extract(granted.safe_body_json,'$.stoppedSha256')=$sha
                      AND NOT EXISTS(SELECT 1 FROM engine_workflow_history used WHERE used.run_id=granted.run_id AND used.kind='redo_round_consumed'
                        AND json_extract(used.safe_body_json,'$.grantId')=granted.id)
                    ORDER BY granted.history_seq DESC LIMIT 1
                    """, r => new PendingRoundGrant(r.Text(0), JsonSerializer.Deserialize(r.Text(1), AdditionalRoundJson.Default.AdditionalRoundGrant)!),
                    new SqlArg("$run", target.Run), new("$target", targetStep), new("$generation", target.Generation),
                    new("$stopped", stopped.Id), new("$sha", CommandStepTransitions.Digest(stopped.Body)));
        }
        return (checked(target.Max + consumed + (grant is null ? 0 : 1)), grant);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdditionalRoundGrant))]
[JsonSerializable(typeof(AdditionalRoundConsumption))]
internal sealed partial class AdditionalRoundJson : JsonSerializerContext;
