using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Workflows.Workspaces;

public static class WorkspaceTransitions
{
    internal static CommandResult Apply(IWriteScope s, CommandContext c, WorkspaceBody body)
    {
        CommandResult Refuse(string reason) => new(false, false, reason, c.ExpectedVersion, 0, c.Epoch);
        var i = body.Intent; var command = body.Command;
        if (body.Context != c with { NowMs = 0 } || i.RunId != c.EntityId || c.Actor.Kind != ActorKind.System)
            return Refuse("forbidden");
        var run = ReadRun(s, c.EntityId);
        if (run is null || run.Machine != c.Machine || run.Hash != i.DefinitionHash) return Refuse("wrong_machine");
        if (run.Epoch != c.Epoch) return Refuse("stale_epoch");
        if (run.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (run.Holder != "local" || run.Reserved || run.OwnerState is "foreign" or "ended" or "handing_off" or "lost" ||
            RunTransitions.IsTerminal(run.State) || run.State == "cancelling") return Refuse("owner_reserved");
        if (command.Mode is not ("new" or "parent") || !Oid(command.BaseHead) ||
            i.Repository != run.Root || i.AllocationId != command.AllocationId || i.StepId != command.StepId ||
            i.Generation != command.Generation || i.BaseHead != command.BaseHead) return Refuse("bad_args");
        var existing = s.QueryFirst("""
            SELECT allocation_id FROM engine_workflow_workspace WHERE run_id=$run AND step_id IS $step
            AND state IN ('pending','ready')
            AND EXISTS(SELECT 1 FROM engine_outbox allocation WHERE allocation.kind='workflow_workspace'
              AND allocation.run_id=engine_workflow_workspace.run_id
              AND json_extract(allocation.payload_json,'$.allocationId')=engine_workflow_workspace.allocation_id
              AND json_extract(allocation.payload_json,'$.generation')=$generation)
            ORDER BY created_ms LIMIT 1
            """, r => r.Text(0), new SqlArg("$run", c.EntityId), new("$step", command.StepId), new("$generation", command.Generation));
        if (existing is not null) return new(true, false, null, run.Version, 0, run.Epoch);
        if (command.StepId is null)
        {
            if (command.Mode != "new" || command.Generation != 0 || run.Setup != "pending" || body.SetupEffectId is null ||
                s.QueryFirst("""
                    SELECT COUNT(*) FROM engine_outbox WHERE id=$id AND kind='workflow_setup' AND run_id=$run AND state='inflight'
                      AND json_extract(payload_json,'$.definitionHash')=$hash
                    """, r => r.Long(0), new SqlArg("$id", body.SetupEffectId), new("$run", i.RunId), new("$hash", i.DefinitionHash)) != 1)
                return Refuse("invalid_transition");
        }
        else
        {
            var step = s.QueryFirst("SELECT generation,state,definition_key FROM engine_step WHERE id=$step AND run_id=$run",
                r => new { Generation = r.Long(0), State = r.Text(1), Key = r.Text(2) }, new SqlArg("$step", command.StepId), new("$run", i.RunId));
            if (run.Setup != "ready" || step is null || step.State != "pending" || step.Generation != command.Generation ||
                step.Key != body.StepKey ||
                command.BaseHead != run.Head || s.QueryFirst("""
                    SELECT COUNT(*) FROM engine_step_dependency d JOIN engine_step predecessor ON predecessor.id=d.dependency_id
                    WHERE d.step_id=$step AND predecessor.state NOT IN ('succeeded','skipped')
                    """, r => r.Long(0), new SqlArg("$step", command.StepId)) > 0) return Refuse("workspace_not_ready");
        }
        s.Execute("""
            INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,branch,base_head,owned,state)
            VALUES($allocation,1,$machine,$now,$now,$run,$step,$allocation,$path,$branch,$base,$owned,'pending');
            UPDATE engine_run SET version=version+1,updated_ms=$now,proof_dir=$proof,pr_body=$pr WHERE id=$run;
            """, new("$allocation", i.AllocationId), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", i.RunId),
            new("$step", i.StepId), new("$path", i.Path), new("$branch", i.Branch), new("$base", i.BaseHead), new("$owned", i.Owned ? 1 : 0),
            new("$proof", i.ProofDir), new("$pr", Path.Combine(i.ProofDir, "pr-body.md")));
        if (i.StepId is null)
            s.Execute("UPDATE engine_run SET worktree=$path,branch=$branch WHERE id=$run",
                new("$path", i.Path), new("$branch", i.Branch), new("$run", i.RunId));
        List<EventDraft> events = [Changed(i, 1, "pending"), RunChanged(i.RunId, run.Version + 1, run.Owner, run.State)];
        if (i.StepId is not null)
        {
            var stepVersion = s.QueryFirst("""
                UPDATE engine_step SET workspace_id=$allocation,version=version+1,updated_ms=$now WHERE id=$step RETURNING version
                """, r => r.Long(0), new SqlArg("$allocation", i.AllocationId), new("$step", i.StepId), new("$now", c.NowMs));
            events.Add(new("step.changed", "step", i.StepId, stepVersion, false,
                new(null, i.RunId, i.StepId, run.Owner, "pending", run.Epoch)));
        }
        History(s, c, i, "workspace_preparing", JsonSerializer.Serialize(i, EngineJson.Default.WorkspaceIntent));
        return EngineJournal.Commit(s, c, run.Version + 1, run.Epoch, events,
            [new("", "workflow_workspace", null, i.RunId, JsonSerializer.SerializeToElement(i, EngineJson.Default.WorkspaceIntent))]);
    }

    public static bool ValidConfirmation(IReadScope s, Effect effect, EffectReceipt receipt)
    {
        try
        {
            if (effect.Kind == "workflow_setup")
            {
                var setup = effect.Payload.Deserialize(EngineJson.Default.WorkflowSetupIntent);
                var data = receipt.Data.Deserialize(EngineJson.Default.WorkspaceSetupData);
                if (setup is null || data is null || effect.RunId != setup.RunId) return false;
                if (data.Error is not null) return data.AllocationId is null && s.QueryFirst("SELECT COUNT(*) FROM engine_run WHERE id=$run AND definition_hash=$hash", r => r.Long(0), new SqlArg("$run", setup.RunId), new("$hash", setup.DefinitionHash)) == 1;
                return s.QueryFirst("""
                    SELECT COUNT(*) FROM engine_workflow_workspace w JOIN engine_run r ON r.id=w.run_id
                    WHERE w.allocation_id=$allocation AND w.run_id=$run AND w.step_id IS NULL AND r.definition_hash=$hash
                    """, r => r.Long(0), new SqlArg("$allocation", data.AllocationId), new("$run", setup.RunId), new("$hash", setup.DefinitionHash)) == 1;
            }
            var i = effect.Payload.Deserialize(EngineJson.Default.WorkspaceIntent);
            var answer = receipt.Data.Deserialize(EngineJson.Default.WorkspacePrepared);
            return i is not null && answer is not null && effect.RunId == i.RunId &&
                answer.AllocationId == i.AllocationId && answer.Path == i.Path && answer.Branch == i.Branch && answer.BaseHead == i.BaseHead &&
                (answer.Error is null ? answer.Head == i.BaseHead : answer.Head == "") && s.QueryFirst("""
                    SELECT COUNT(*) FROM engine_workflow_workspace w JOIN engine_run r ON r.id=w.run_id
                    WHERE w.allocation_id=$allocation AND w.run_id=$run AND w.step_id IS $step AND w.path=$path AND w.branch=$branch
                      AND w.base_head=$base AND w.state='pending' AND r.definition_hash=$hash
                    """, r => r.Long(0), new SqlArg("$allocation", i.AllocationId), new("$run", i.RunId), new("$step", i.StepId),
                    new("$path", i.Path), new("$branch", i.Branch), new("$base", i.BaseHead), new("$hash", i.DefinitionHash)) == 1;
        }
        catch (JsonException) { return false; }
    }

    public static void Confirm(IWriteScope s, CommandContext c, Effect effect, EffectReceipt receipt)
    {
        if (effect.Kind == "workflow_setup")
        {
            var setup = effect.Payload.Deserialize(EngineJson.Default.WorkflowSetupIntent)!;
            var data = receipt.Data.Deserialize(EngineJson.Default.WorkspaceSetupData)!;
            var owner = ReadRun(s, setup.RunId)!;
            if (data.Error is not null && !RunTransitions.IsTerminal(owner.State) && owner.State != "cancelling")
            {
                s.Execute("UPDATE engine_run SET setup_state='failed',version=version+1,updated_ms=$now WHERE id=$run",
                    new("$now", c.NowMs), new("$run", setup.RunId));
                EngineJournal.Commit(s, c, owner.Version + 1, owner.Epoch,
                    [RunChanged(setup.RunId, owner.Version + 1, owner.Owner, owner.State)], []);
                s.Execute("""
                    INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
                    SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,0,'setup_failed',$now,$body
                    FROM engine_workflow_history WHERE run_id=$run
                    """, new("$id", c.CommandId.ToString("N") + "/setup_failed"), new("$machine", c.Machine), new("$now", c.NowMs),
                    new("$run", setup.RunId), new("$body", JsonSerializer.Serialize(data, EngineJson.Default.WorkspaceSetupData)));
            }
            return;
        }
        var i = effect.Payload.Deserialize(EngineJson.Default.WorkspaceIntent)!;
        var result = receipt.Data.Deserialize(EngineJson.Default.WorkspacePrepared)!;
        var state = result.Error is null ? "ready" : "failed";
        var version = s.QueryFirst("""
            UPDATE engine_workflow_workspace SET state=$state,head=$head,error=$error,version=version+1,updated_ms=$now
            WHERE allocation_id=$allocation RETURNING version
            """, r => r.Long(0), new SqlArg("$state", state), new("$head", result.Head), new("$error", result.Error),
            new("$now", c.NowMs), new("$allocation", i.AllocationId));
        var run = ReadRun(s, i.RunId)!;
        List<EventDraft> events = [Changed(i, version, state)];
        if (i.StepId is null && !RunTransitions.IsTerminal(run.State) && run.State != "cancelling")
        {
            s.Execute("UPDATE engine_run SET setup_state=$state,work_head=$head,version=version+1,updated_ms=$now WHERE id=$run",
                new("$state", state), new("$head", result.Error is null ? result.Head : null), new("$now", c.NowMs), new("$run", i.RunId));
            events.Add(RunChanged(i.RunId, run.Version + 1, run.Owner, run.State));
            if (result.Error is null) events.AddRange(WorkflowRetrySeeds.Apply(s, c, i.RunId));
        }
        History(s, c, i, "workspace_" + state, JsonSerializer.Serialize(result, EngineJson.Default.WorkspacePrepared));
        EngineJournal.Commit(s, c, version, c.Epoch, events, []);
    }

    private static void History(IWriteScope s, CommandContext c, WorkspaceIntent i, string kind, string body) => s.Execute("""
        INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,generation,kind,at_ms,safe_body_json)
        SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$generation,$kind,$now,$body
        FROM engine_workflow_history WHERE run_id=$run
        """, new("$id", c.CommandId.ToString("N") + "/" + kind), new("$machine", c.Machine), new("$now", c.NowMs),
        new("$run", i.RunId), new("$step", i.StepId), new("$generation", i.Generation), new("$kind", kind), new("$body", body));
    private static bool Oid(string value) => value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record Run(long Version, string Machine, string Owner, string State, string Setup, string Root,
        string Hash, string? Head, long Epoch, string Holder, string OwnerState, bool Reserved);
    private static Run? ReadRun(IReadScope s, string id) => s.QueryFirst("""
        SELECT r.version,r.machine,r.owner_session_id,r.state,r.setup_state,r.project_root,r.definition_hash,r.work_head,
          se.epoch,se.holder,se.state,se.reserved_epoch FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
        """, r => new Run(r.Long(0), r.Text(1), r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Text(6), r.IsNull(7) ? null : r.Text(7),
            r.Long(8), r.Text(9), r.Text(10), !r.IsNull(11)), new SqlArg("$id", id));
    private static EventDraft Changed(WorkspaceIntent i, long version, string state) => new("workspace.changed", "workspace", i.AllocationId,
        version, false, new(null, i.RunId, i.StepId, null, state, null));
    private static EventDraft RunChanged(string id, long version, string owner, string state) => new("run.changed", "run", id,
        version, false, new(owner, id, null, null, state, null));
}

public sealed record WorkspaceSetupData(string? AllocationId, string? Error = null);
