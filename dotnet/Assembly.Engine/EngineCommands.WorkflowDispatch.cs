using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Scheduling;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    internal DispatchAdmission? ReadWorkflowAdmission(IReadScope scope, string stepId) =>
        WorkflowDispatch.ReadAdmission(scope, stepId, _limits.ReadDepth(), _limits.ReadChildren(), _accountContext, _isConfiguredProvider);

    public WorkflowDispatchAck ApplyPreparedDispatch(IWriteScope scope, CommandContext context, PreparedWorkflowDispatch prepared) =>
        WorkflowDispatch.ApplyPreparedDispatch(scope, context, prepared);

    internal PreparedWorkflowDispatch? PrepareWorkflowDispatch(IReadScope scope, string stepId, long nowMs, string checkedHead, string checkoutFingerprint)
    {
        var admission = ReadWorkflowAdmission(scope, stepId);
        if (admission is null || !admission.Ready) return null;
        var w = admission.Witness;
        if (w.Machine != _machine || w.Head != checkedHead || checkoutFingerprint.Length != 64 || !checkoutFingerprint.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("workflow_checkout_not_checked");
        var latest = WorkflowDispatch.Latest(scope, w);
        if (latest is not null)
        {
            var existing = scope.QueryFirst("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", r => r.Text(0), new SqlArg("$id", latest.CommandId.ToString("N")));
            if (existing is null) throw new InvalidOperationException("workflow_dispatch_receipt_unknown");
            var prior = JsonSerializer.Deserialize(existing, EngineJson.Default.CommandResult)!;
            if (prior.Applied) return null;
            if (admission.Hash == latest.Prepared.Hash && admission.Hash == latest.Authoritative.Hash) return null;
        }
        var source = scope.QueryFirst("SELECT snapshot_json FROM engine_workflow_snapshot WHERE run_id=$run", r => r.Text(0), new SqlArg("$run", w.RunId))!;
        var snapshot = JsonSerializer.Deserialize(source, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var key = snapshot.StepIds.Single(p => p.Value == stepId).Key;
        var spec = snapshot.Snapshot.Definition.Steps[key];
        var values = TemplateValues(scope, snapshot, w.RunId, w.StepId, w.Generation);
        if (spec.Condition is not null && !WorkflowTemplates.Condition(spec.Condition, values)) return null;
        var prompt = WorkflowTemplates.Render(snapshot.Snapshot.Prompts[spec.Prompt!], values);
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("workflow_prompt_empty");
        var tuple = new[] { w.Machine, w.RunId, w.StepId, w.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), w.NextAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var predecessor = latest?.CommandId;
        var commandId = DispatchIdentity.Id("workflow-dispatch-intent/v1", [.. tuple, predecessor?.ToString("N"), admission.Hash]);
        var session = w.Resume?.SessionId ?? DispatchIdentity.Id("workflow-dispatch-session/v1", tuple).ToString("N");
        var message = DispatchIdentity.Id("workflow-dispatch-message/v1", tuple).ToString("N");
        var context = new CommandContext(commandId, _machine, new(ActorKind.System, "engine.worker"), stepId, w.StepVersion, w.OwnerEpoch, nowMs);
        var bytes = Encoding.UTF8.GetBytes(prompt);
        var frozen = WorkflowPreparation.Freeze(new DispatchBody(context, admission, predecessor, session, message, checkoutFingerprint, prompt,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, key, snapshot.Request.Task is { } task && task.TryGetProperty("url", out var url) ? url.GetString() : null), default);
        return new(frozen.Body, frozen.Envelope, this, frozen.Context);
    }

    internal CommandResult ApplyWorkflowDispatchStages(IWriteScope scope, CommandContext context, PreparedWorkflowDispatch prepared)
    {
        var b = prepared.Body; var w = b.Prepared.Witness;
        CommandResult Refuse(string error) => new(false, false, error, w.StepVersion, 0, w.OwnerEpoch);
        var account = w.AccountContext;
        if ((_accountContext?.Invoke(w.Provider) ?? "") != account) return Refuse("version_conflict");
        if (_isConfiguredProvider?.Invoke(w.Provider, account) != true) return Refuse("provider_unavailable");
        var provider = scope.QueryFirst("SELECT installation,auth_state,facts_json FROM engine_provider WHERE machine=$machine AND provider=$provider AND account_context=$account",
            r => new { Installation = r.Text(0), Auth = r.Text(1), Facts = r.IsNull(2) ? null : r.Text(2) }, new SqlArg("$machine", _machine), new("$provider", w.Provider), new("$account", account));
        if (provider is null || provider.Installation != "installed" || provider.Facts is null) return Refuse("provider_unavailable");
        if (provider.Auth is not ("signed_in" or "unverified" or "unsupported")) return Refuse("sign_in_required");
        using var facts = JsonDocument.Parse(provider.Facts);
        if (w.Model is not null && !facts.RootElement.GetProperty("models").EnumerateArray().Any(m => m.GetProperty("id").GetString() == w.Model)) return Refuse("model_unavailable");
        // The accepted StartSession contract has no explicit permission producer yet.
        if (w.Permission is not null) return Refuse("workflow_permission_unavailable");
        var actor = new EngineActor(ActorKind.Session, w.OwnerId);
        var start = new StartSession(w.Provider, w.Model, w.Folder, b.Title, w.OwnerId, w.RunId, b.TaskUrl, ManagedCheckout: false);
        var workerEpoch = w.Resume is null ? 0 : checked(w.Resume.Epoch + 1);
        CommandResult started;
        if (w.Resume is { } resume)
            started = SessionTransitions.ResumeWorkflowWorker(scope, context with
            {
                CommandId = DispatchIdentity.Id("workflow-dispatch-start-stage/v1", context.CommandId.ToString("N")),
                EntityId = resume.SessionId,
                ExpectedVersion = scope.QueryFirst("SELECT version FROM engine_session WHERE id=$id", r => r.Long(0), new SqlArg("$id", resume.SessionId)),
                Epoch = resume.Epoch,
                Actor = new(ActorKind.System, "engine.worker")
            }, resume, start with { ManagedCheckout = resume.ManagedCheckout });
        else
            started = ApplySession(scope, context with
            {
                CommandId = DispatchIdentity.Id("workflow-dispatch-start-stage/v1", context.CommandId.ToString("N")),
                Actor = actor,
                EntityId = b.SessionId,
                ExpectedVersion = 0,
                Epoch = 0
            }, start, startEffectPayload: PreparedStartEffectPayload.Prepare(start));
        if (!started.Applied) return started;
        var stepped = StepTransitions.Apply(scope, context with { CommandId = DispatchIdentity.Id("workflow-dispatch-step-stage/v1", context.CommandId.ToString("N")), Actor = actor, Epoch = workerEpoch }, new StartStep(b.SessionId));
        if (!stepped.Applied) return stepped;
        scope.Execute("""
            UPDATE engine_step_attempt SET generation=$generation,attempt_in_generation=$inGeneration,
              dispatch_command_id=$command,prompt_message_id=$message,rendered_prompt_sha256=$sha,rendered_prompt_bytes=$bytes,
              session_id=$session,authority_machine=$machine,execution_epoch=$executionEpoch,workspace_id=$workspace,fingerprint=$fingerprint
            WHERE step_id=$step AND attempt=$attempt;
            UPDATE engine_step SET attempt_in_generation=$inGeneration,authority_session_id=$session,authority_machine=$machine,
              authority_epoch=$executionEpoch,dispatch_state='starting',deadline_ms=$deadline WHERE id=$step;
            """, new("$generation", w.Generation), new("$inGeneration", w.AttemptInGeneration), new("$command", context.CommandId.ToString("N")),
            new("$message", b.MessageId), new("$sha", b.PromptSha256), new("$bytes", b.PromptBytes), new("$session", b.SessionId), new("$machine", _machine),
            new("$workspace", w.WorkspaceId), new("$executionEpoch", workerEpoch), new("$fingerprint", b.CheckoutFingerprint), new("$step", w.StepId), new("$attempt", w.NextAttempt),
            new("$deadline", checked(context.NowMs + scope.QueryFirst("SELECT step_timeout_ms FROM engine_run WHERE id=$run", r => r.Long(0), new SqlArg("$run", w.RunId)))));
        var sent = MessageTransitions.ApplyPreparedHelper(scope, context with
        {
            CommandId = DispatchIdentity.Id("workflow-dispatch-message-stage/v1", context.CommandId.ToString("N")),
            Actor = actor,
            EntityId = b.MessageId,
            ExpectedVersion = 0,
            Epoch = workerEpoch
        }, new(w.OwnerId, b.SessionId, b.Prompt, "prompt"), Encoding.UTF8.GetBytes(b.Prompt), _messageLimit(), _expiryPolicy);
        if (sent.Applied) scope.Execute("UPDATE engine_message SET held_epoch=$epoch,target_epoch=$epoch WHERE id=$id",
            new("$epoch", workerEpoch), new("$id", b.MessageId));
        return sent;
    }

    internal static JsonElement TemplateValues(IReadScope scope, WorkflowStartBody source, string runId, string stepId, int generation)
    {
        var redo = ReviewTransitions.RedoValues(scope, runId, stepId, generation);
        var run = scope.QueryFirst("SELECT proof_dir,pr_body FROM engine_run WHERE id=$run", r => (Proof: r.IsNull(0) ? null : r.Text(0), Body: r.IsNull(1) ? null : r.Text(1)), new SqlArg("$run", runId));
        var receipts = scope.Query("""
            SELECT st.definition_key,json_extract(receipt.body_json,'$.report.receipt'),
              (SELECT json_extract(history.safe_body_json,'$.files') FROM engine_workflow_history history
               WHERE history.run_id=st.run_id AND history.kind='receipt_checked'
                 AND json_extract(history.safe_body_json,'$.receiptId')=receipt.id ORDER BY history.history_seq DESC LIMIT 1)
            FROM engine_step st JOIN engine_workflow_receipt receipt ON receipt.id=st.receipt_id
            WHERE st.run_id=$run AND receipt.state='accepted' ORDER BY st.definition_key COLLATE BINARY
            """, r => (Key: r.Text(0), Body: r.Text(1), Files: r.IsNull(2) ? "[]" : r.Text(2)), new SqlArg("$run", runId));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WritePropertyName("inputs"); source.Inputs.Values.WriteTo(writer);
            if (redo is not null) { writer.WritePropertyName("redo"); redo.Values.WriteTo(writer); }
            writer.WritePropertyName("task"); if (source.Request.Task is { } task) task.WriteTo(writer); else writer.WriteNullValue();
            writer.WriteStartObject("run"); writer.WriteString("proof_dir", run.Proof); writer.WriteString("pr_body", run.Body); writer.WriteEndObject();
            writer.WriteStartObject("steps"); foreach (var receipt in receipts) { writer.WriteStartObject(receipt.Key); using var document = JsonDocument.Parse(receipt.Body); foreach (var field in document.RootElement.EnumerateObject()) field.WriteTo(writer); writer.WriteStartArray("files"); using var files = JsonDocument.Parse(receipt.Files); foreach (var file in files.RootElement.EnumerateArray()) writer.WriteStringValue(file.GetProperty("path").GetString()); writer.WriteEndArray(); writer.WriteEndObject(); }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var result = JsonDocument.Parse(stream.ToArray()); return result.RootElement.Clone();
    }
}
