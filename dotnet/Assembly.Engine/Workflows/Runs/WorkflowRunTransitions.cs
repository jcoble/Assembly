using System.Text.Json;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Runs;

internal static class WorkflowRunTransitions
{
    internal static CommandResult Start(IWriteScope scope, CommandContext context, PreparedWorkflowCommand prepared)
    {
        var body = prepared.Start; var request = body.Request;
        if (request.CommandId != context.CommandId || request.ProposedRunId != context.EntityId ||
            request.ExpectedVersion != context.ExpectedVersion || request.Epoch != context.Epoch || context.ExpectedVersion != 0 ||
            request.Machine is not null && request.Machine != context.Machine ||
            string.IsNullOrWhiteSpace(request.OwnerSessionId) || string.IsNullOrWhiteSpace(request.RequestKey) ||
            string.IsNullOrWhiteSpace(request.Request) || !Guid.TryParse(request.ProposedRunId, out _) ||
            !ValidPath(request.Path)) return Refuse(context, "bad_args");
        if (!RunTransitions.CanControl(context, request.OwnerSessionId)) return Refuse(context, "forbidden");
        var existing = WorkflowStarts.Read(scope, request);
        if (existing is not null)
            return existing.Fingerprint == body.RequestFingerprint
                ? JsonSerializer.Deserialize(existing.Result, EngineJson.Default.CommandResult)!
                : Refuse(context, "request_key_conflict");
        var owner = scope.QueryFirst("""
            SELECT parent_id,state,machine,holder,reserved_epoch,project_id FROM engine_session WHERE id=$owner
            """, r => new { Parent = r.IsNull(0) ? null : r.Text(0), State = r.Text(1), Machine = r.Text(2), Holder = r.Text(3), Reserved = !r.IsNull(4), Project = r.IsNull(5) ? null : r.Text(5) },
            new SqlArg("$owner", request.OwnerSessionId));
        if (owner is null) return Refuse(context, "bad_args");
        if (owner.Parent is not null || owner.Reserved) return Refuse(context, "owner_reserved");
        if (owner.Machine != context.Machine || owner.Holder != "local" || owner.State is "foreign" or "ended" or "handing_off") return Refuse(context, "owner_reserved");
        if (scope.QueryFirst("SELECT COUNT(*) FROM projects WHERE id=$id AND machine=$machine AND root_path=$root AND removed_at IS NULL",
            r => r.Long(0), new SqlArg("$id", body.ProjectId), new("$machine", context.Machine), new("$root", body.ProjectRoot)) != 1)
            return Refuse(context, "project_not_found");
        if (!TryTaskUrl(request.Task, out var taskUrl)) return Refuse(context, "bad_args");
        var steps = body.Snapshot.Definition.Steps.Values.Select(step => new StepDefinition(body.StepIds[step.Id], step.Id,
            step.Required, step.MaxAttempts, step.Condition is not null,
            step.Needs.Select(key => new Dependency(body.StepIds[key], false)).ToArray())).ToArray();
        var created = RunTransitions.StartWorkflow(scope, context, new StartRun(request.OwnerSessionId, taskUrl, null, null, 30000, steps),
            new(context.EntityId, body.Snapshot.Hash));
        if (!created.Applied) return created;
        var title = request.Task is { } task && task.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
            ? titleValue.GetString()! : request.Request;
        title = string.Concat(title.EnumerateRunes().Take(120).Select(rune => rune.ToString()));
        scope.Execute("""
            UPDATE engine_run SET title=$title,request=$request,project_id=$project,project_root=$root,definition_path=$path,
              definition_hash=$hash,setup_state='pending',max_sessions=$sessions,step_timeout_ms=$timeout,max_rounds=$rounds
            WHERE id=$run;
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,task_url,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES($run,1,$machine,$now,$now,$task,$run,1,$hash,$inputs,$body);
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,task_url,run_id,history_seq,generation,kind,at_ms,safe_body_json)
            VALUES($history,1,$machine,$now,$now,$task,$run,1,0,'created',$now,'{}');
            """, new("$title", title), new("$request", request.Request), new("$project", body.ProjectId), new("$root", body.ProjectRoot),
            new("$path", request.Path), new("$hash", body.Snapshot.Hash), new("$run", context.EntityId), new("$machine", context.Machine),
            new("$now", context.NowMs), new("$task", taskUrl), new("$inputs", body.Inputs.Hash), new("$body", prepared.BodyJson),
            new("$history", context.CommandId.ToString("N") + "/created"), new("$sessions", body.Snapshot.Definition.Limits.MaxSessions),
            new("$timeout", body.Snapshot.Definition.Limits.StepTimeoutMilliseconds), new("$rounds", body.Snapshot.Definition.Limits.MaxRounds));
        foreach (var spec in body.Snapshot.Definition.Steps.Values)
            scope.Execute("""
                UPDATE engine_step SET definition_key=$key,kind=$kind,result=$result,authority_machine=$machine,
                  dispatch_state='pending' WHERE id=$id
                """, new("$key", spec.Id), new("$kind", spec.Kind), new("$result", spec.Result),
                new("$machine", context.Machine), new("$id", body.StepIds[spec.Id]));
        return created;
    }

    private static bool ValidPath(string path) =>
        Path.GetDirectoryName(path)?.Replace('\\', '/') == ".assembly/workflows" && Path.GetExtension(path) == ".yml";
    private static bool TryTaskUrl(JsonElement? task, out string? url)
    {
        url = null;
        if (task is null) return true;
        if (task is not { ValueKind: JsonValueKind.Object } value) return false;
        if (!value.TryGetProperty("url", out var supplied) || supplied.ValueKind == JsonValueKind.Null) return true;
        if (supplied.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(supplied.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        url = supplied.GetString();
        return true;
    }
    private static CommandResult Refuse(CommandContext c, string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
}

public sealed record WorkflowSetupIntent(string RunId, string DefinitionHash);
