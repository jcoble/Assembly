using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Workflows.Scheduling;

public static partial class WorkflowDispatch
{
    public static WorkflowDispatchAck ApplyPreparedDispatch(IWriteScope scope, CommandContext context, PreparedWorkflowDispatch prepared)
    {
        var body = prepared.Body; var w = body.Prepared.Witness;
        CommandResult Refuse(string error) => new(false, false, error, context.ExpectedVersion, 0, context.Epoch);
        WorkflowDispatchAck Refused(string error) => new(w.RunId, w.StepId, w.Generation, w.NextAttempt, null, null, Refuse(error));
        if (context with { NowMs = 0 } != body.Context || context.Actor != new EngineActor(ActorKind.System, "engine.worker") || prepared.Commands is null)
            return Refused("forbidden");
        var existing = scope.QueryFirst("""
            SELECT safe_body_json FROM engine_workflow_history WHERE run_id=$run AND kind='dispatch_intent'
              AND json_extract(safe_body_json,'$.commandId')=$command
            """, r => JsonSerializer.Deserialize(r.Text(0), DispatchIntentJson.Default.DispatchIntent), new SqlArg("$run", w.RunId), new("$command", context.CommandId.ToString()));
        if (existing is not null)
        {
            // Submitted content still passes the same journal fingerprint check, even on explicit replay.
            var original = Replay(scope, existing);
            var checkedResult = EngineJournal.Execute(scope, context, prepared.Envelope.CanonicalJournal, () => throw new InvalidOperationException("Replay executed."));
            return checkedResult.Error == "command_id_conflict" ? Refused("command_id_conflict") : original;
        }
        if (scope.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id", r => r.Long(0), new SqlArg("$id", context.CommandId.ToString("N"))) != 0)
            throw new InvalidOperationException("workflow_dispatch_metadata_unknown");
        var latest = Latest(scope, w);
        if (latest is not null)
        {
            var ack = Replay(scope, latest);
            if (ack.Result.Applied || body.Predecessor != latest.CommandId) return ack;
        }
        else if (body.Predecessor is not null) throw new InvalidOperationException("workflow_dispatch_predecessor_unknown");
        var admission = prepared.Commands.ReadWorkflowAdmission(scope, w.StepId)
            ?? throw new InvalidOperationException("workflow_dispatch_admission_unknown");
        if (latest is not null && (!admission.Ready || admission.Hash == latest.Prepared.Hash && admission.Hash == latest.Authoritative.Hash))
            return Replay(scope, latest);
        var intent = new DispatchIntent(1, context.CommandId, body.Predecessor, w.RunId, w.StepId, w.Generation, w.NextAttempt,
            body.Prepared, admission, context, prepared.Envelope.CanonicalJournal, prepared.Envelope.Sha256, prepared.Envelope.BodyBytes);
        var result = EngineJournal.Execute(scope, context, prepared.Envelope.CanonicalJournal, () =>
        {
            if (!admission.Ready) return Refuse(admission.Reason ?? "not_ready");
            if (admission.Hash != body.Prepared.Hash) return Refuse("version_conflict");
            scope.Execute("SAVEPOINT workflow_dispatch_stages");
            var stages = new StageScope(scope);
            var staged = prepared.Commands.ApplyWorkflowDispatchStages(stages, context, prepared);
            if (!staged.Applied)
            {
                scope.Execute("ROLLBACK TO SAVEPOINT workflow_dispatch_stages");
                scope.Execute("RELEASE SAVEPOINT workflow_dispatch_stages");
                return staged;
            }
            scope.Execute("RELEASE SAVEPOINT workflow_dispatch_stages");
            stages.Flush();
            return staged;
        });
        AppendIntent(scope, intent);
        return Ack(scope, intent, result);
    }

    // Reads and SQL stay on the identical supplied writer. Only publications are staged,
    // with their values frozen at call time, so rollback cannot leak projected chat rows.
    private sealed class StageScope(IWriteScope outer) : IWriteScope
    {
        private readonly List<Action> _publish = [];
        public void AfterCommit(Action publishState) => _publish.Add(() => outer.AfterCommit(publishState));
        public int Execute(string sql, params SqlArg[] args) => outer.Execute(sql, args);
        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => outer.Query(sql, map, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => outer.QueryFirst(sql, map, args);
        public void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type, bool keyedOnly = false)
        {
            var frozen = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(change, type), type)!;
            _publish.Add(() => outer.Publish(topic, key, frozen, type, keyedOnly));
        }
        public void Flush() { foreach (var publish in _publish) publish(); _publish.Clear(); }
    }
}
