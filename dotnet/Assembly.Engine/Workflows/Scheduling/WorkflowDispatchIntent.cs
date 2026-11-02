using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Scheduling;

internal sealed record DispatchIntent(int Format, Guid CommandId, Guid? PredecessorRefusalCommandId,
    string RunId, string StepId, int Generation, int Attempt, DispatchAdmission Prepared, DispatchAdmission Authoritative,
    CommandContext Context, string CanonicalEnvelope, string Sha256, long BodyBytes);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DispatchIntent))]
internal sealed partial class DispatchIntentJson : JsonSerializerContext;

public sealed record WorkflowDispatchAck(string RunId, string StepId, int Generation, int Attempt,
    string? SessionId, string? MessageId, CommandResult Result);

public static partial class WorkflowDispatch
{
    internal static DispatchIntent? Latest(IReadScope s, DispatchEligibility witness) => s.QueryFirst("""
        SELECT safe_body_json FROM engine_workflow_history WHERE run_id=$run AND step_id=$step
          AND generation=$generation AND attempt=$attempt AND kind='dispatch_intent'
        ORDER BY history_seq DESC LIMIT 1
        """, r => JsonSerializer.Deserialize(r.Text(0), DispatchIntentJson.Default.DispatchIntent),
        new SqlArg("$run", witness.RunId), new("$step", witness.StepId), new("$generation", witness.Generation), new("$attempt", witness.NextAttempt));

    internal static WorkflowDispatchAck Replay(IWriteScope s, DispatchIntent intent)
    {
        if (intent.Format != 1 || intent.Context.CommandId != intent.CommandId || intent.Context.EntityId != intent.StepId ||
            intent.Prepared.Witness.RunId != intent.RunId || intent.Prepared.Witness.StepId != intent.StepId ||
            intent.Prepared.Witness.Generation != intent.Generation || intent.Prepared.Witness.NextAttempt != intent.Attempt)
            throw new InvalidOperationException("workflow_dispatch_intent_unknown");
        var ancestry = s.QueryFirst("""
            WITH RECURSIVE chain(command_id,predecessor,history_seq,path,invalid) AS (
              SELECT json_extract(safe_body_json,'$.commandId'),json_extract(safe_body_json,'$.predecessorRefusalCommandId'),history_seq,
                '|' || json_extract(safe_body_json,'$.commandId') || '|',0
              FROM engine_workflow_history WHERE run_id=$run AND step_id=$step AND generation=$generation AND attempt=$attempt
                AND kind='dispatch_intent' AND json_extract(safe_body_json,'$.commandId')=$command
              UNION ALL
              SELECT json_extract(h.safe_body_json,'$.commandId'),json_extract(h.safe_body_json,'$.predecessorRefusalCommandId'),h.history_seq,
                chain.path || json_extract(h.safe_body_json,'$.commandId') || '|',
                CASE WHEN h.history_seq>=chain.history_seq OR instr(chain.path,'|' || json_extract(h.safe_body_json,'$.commandId') || '|')>0 THEN 1 ELSE 0 END
              FROM engine_workflow_history h JOIN chain ON json_extract(h.safe_body_json,'$.commandId')=chain.predecessor
              WHERE h.run_id=$run AND h.step_id=$step AND h.generation=$generation AND h.attempt=$attempt
                AND h.kind='dispatch_intent' AND chain.invalid=0
                AND EXISTS(SELECT 1 FROM engine_command_receipt receipt WHERE receipt.command_id=replace(json_extract(h.safe_body_json,'$.commandId'),'-','')
                  AND json_extract(receipt.result_json,'$.applied')=0))
            SELECT COUNT(*),COALESCE(SUM(invalid),0),COALESCE(SUM(CASE WHEN predecessor IS NULL THEN 1 ELSE 0 END),0) FROM chain
            """, r => (Count: r.Long(0), Invalid: r.Long(1), Roots: r.Long(2)), new SqlArg("$run", intent.RunId), new("$step", intent.StepId),
            new("$generation", intent.Generation), new("$attempt", intent.Attempt), new("$command", intent.CommandId.ToString()));
        if (ancestry.Count == 0 || ancestry.Invalid != 0 || ancestry.Roots != 1) throw new InvalidOperationException("workflow_dispatch_predecessor_unknown");
        var receipt = s.QueryFirst("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", r => r.Text(0), new SqlArg("$id", intent.CommandId.ToString("N")));
        if (receipt is null) throw new InvalidOperationException("workflow_dispatch_receipt_unknown");
        var result = EngineJournal.Execute(s, intent.Context, intent.CanonicalEnvelope, () => throw new InvalidOperationException("Dispatch replay cannot execute."));
        if (result.Error == "command_id_conflict") throw new InvalidOperationException("workflow_dispatch_intent_corrupt");
        return Ack(s, intent, result);
    }

    private static WorkflowDispatchAck Ack(IReadScope s, DispatchIntent intent, CommandResult result)
    {
        if (!result.Applied) return new(intent.RunId, intent.StepId, intent.Generation, intent.Attempt, null, null, result);
        var binding = s.QueryFirst("""
            SELECT session_id,prompt_message_id FROM engine_step_attempt
            WHERE step_id=$step AND attempt=$attempt AND generation=$generation AND dispatch_command_id=$command
            """, r => (Session: r.IsNull(0) ? null : r.Text(0), Message: r.IsNull(1) ? null : r.Text(1)), new SqlArg("$step", intent.StepId),
            new("$attempt", intent.Attempt), new("$generation", intent.Generation), new("$command", intent.CommandId.ToString("N")));
        if (binding.Session is null || binding.Message is null) throw new InvalidOperationException("workflow_dispatch_binding_unknown");
        return new(intent.RunId, intent.StepId, intent.Generation, intent.Attempt, binding.Session, binding.Message, result);
    }

    private static void AppendIntent(IWriteScope s, DispatchIntent intent)
    {
        s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'dispatch_intent',$now,$body
            FROM engine_workflow_history WHERE run_id=$run
            """, new("$id", intent.CommandId.ToString("N") + "/dispatch_intent"), new("$machine", intent.Context.Machine), new("$now", intent.Context.NowMs),
            new("$run", intent.RunId), new("$step", intent.StepId), new("$attempt", intent.Attempt), new("$generation", intent.Generation),
            new("$body", JsonSerializer.Serialize(intent, DispatchIntentJson.Default.DispatchIntent)));
    }
}
