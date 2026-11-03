using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine.Workflows.Scheduling;

internal sealed record DispatchDependency(string Id, bool Optional, bool RuleSkippable, long Version,
    string State, int Generation, int Attempt, string? ReceiptHash, string? Head, string? RuleReason);
internal sealed record DispatchChild(string Id, string State, long Epoch, bool CancelRequested);
internal sealed record DispatchAncestor(string Id, string? ParentId);
internal sealed record DispatchProvider(bool Configured, string? Installation, string? Authentication, bool ModelAvailable);
internal sealed record DispatchReservation(string RunId, string StepId, int Generation, int Attempt,
    string? SessionId, string? Machine, long? Epoch, string State);
internal sealed record DispatchEligibility(string Machine, string RunId, string RunMachine, long RunVersion, string RunState,
    string StepId, long StepVersion, string StepState, int Generation, int Attempt, int NextAttempt, int AttemptInGeneration,
    string DefinitionHash, string OwnerId, long OwnerEpoch, string OwnerState, string OwnerActivity, bool OwnerCancelled,
    string? AuthorityMachine, string? AuthoritySession, long? AuthorityEpoch, string WorkspaceId, long WorkspaceVersion,
    string WorkspaceState, string Folder, string? BaseHead, string? Head, string SourceDigest, string Provider, string? Model,
    string? Permission, string AccountContext, DispatchProvider ProviderFacts, int DepthLimit, int ChildLimit, long RunLimit, long SharedLimit,
    ImmutableArray<DispatchAncestor> Ancestors, ImmutableArray<DispatchChild> Children,
    ImmutableArray<DispatchDependency> Dependencies, ImmutableArray<DispatchReservation> Reservations, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowResumeProof? Resume);
internal sealed record DispatchAdmission(DispatchEligibility Witness, string Hash, bool Ready, string? Reason);
internal sealed record DispatchBody(CommandContext Context, DispatchAdmission Prepared, Guid? Predecessor,
    string SessionId, string MessageId, string CheckoutFingerprint, string Prompt, string PromptSha256, long PromptBytes, string Title, string? TaskUrl);

public sealed class PreparedWorkflowDispatch
{
    internal DispatchBody Body { get; }
    internal PreparedWorkflowCommand Envelope { get; }
    internal EngineCommands? Commands { get; }
    public CommandContext Context { get; }
    internal PreparedWorkflowDispatch(DispatchBody body, PreparedWorkflowCommand envelope, EngineCommands? commands = null, CommandContext? context = null) { Body = body; Envelope = envelope; Commands = commands; Context = context ?? body.Context; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DispatchBody))]
[JsonSerializable(typeof(DispatchEligibility))]
internal sealed partial class DispatchJson : JsonSerializerContext;

internal static class DispatchIdentity
{
    internal static string Hash(string domain, params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in new[] { domain }.Concat(fields))
        {
            hash.AppendData([field is null ? (byte)0 : (byte)1]);
            if (field is null) continue;
            var bytes = Encoding.UTF8.GetBytes(field); Span<byte> length = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength); hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static Guid Id(string domain, params string?[] fields) => new(Convert.FromHexString(Hash(domain, fields))[..16]);
}

public static partial class WorkflowDispatch
{
    internal static DispatchAdmission? ReadAdmission(IReadScope scope, string stepId, int depthLimit, int childLimit, Func<string, string>? accountContext = null, Func<string, string, bool>? configured = null)
    {
        var row = scope.QueryFirst("""
            SELECT meta.machine_id,run.id,run.machine,run.version,run.state,st.version,st.state,st.generation,st.attempt,
              st.attempt_in_generation,run.definition_hash,owner.id,owner.epoch,owner.state,owner.activity_state,owner.cancel_requested,
              st.authority_machine,st.authority_session_id,st.authority_epoch,w.id,w.version,w.state,w.path,w.base_head,w.head,
              snapshot.snapshot_json,run.max_sessions,run.paused,run.setup_state,owner.holder,owner.reserved_epoch,st.kind,st.dispatch_state,st.session_id
            FROM engine_step st JOIN engine_run run ON run.id=st.run_id JOIN engine_session owner ON owner.id=run.owner_session_id
            JOIN engine_workflow_snapshot snapshot ON snapshot.run_id=run.id
            JOIN engine_workflow_workspace w ON w.id=st.workspace_id JOIN engine_meta meta ON meta.singleton=1
            WHERE st.id=$step
            """, r => new
        {
            Machine = r.Text(0),
            Run = r.Text(1),
            RunMachine = r.Text(2),
            RunVersion = r.Long(3),
            RunState = r.Text(4),
            StepVersion = r.Long(5),
            StepState = r.Text(6),
            Generation = (int)r.Long(7),
            Attempt = (int)r.Long(8),
            InGeneration = (int)r.Long(9),
            Hash = r.Text(10),
            Owner = r.Text(11),
            OwnerEpoch = r.Long(12),
            OwnerState = r.Text(13),
            Activity = r.Text(14),
            Cancelled = r.Long(15) != 0,
            AuthorityMachine = r.IsNull(16) ? null : r.Text(16),
            AuthoritySession = r.IsNull(17) ? null : r.Text(17),
            AuthorityEpoch = r.IsNull(18) ? (long?)null : r.Long(18),
            Workspace = r.Text(19),
            WorkspaceVersion = r.Long(20),
            WorkspaceState = r.Text(21),
            Folder = r.IsNull(22) ? "" : r.Text(22),
            Base = r.IsNull(23) ? null : r.Text(23),
            Head = r.IsNull(24) ? null : r.Text(24),
            Snapshot = r.Text(25),
            RunLimit = r.Long(26),
            Paused = r.Long(27) != 0,
            Setup = r.Text(28),
            Holder = r.Text(29),
            Reserved = !r.IsNull(30),
            Kind = r.Text(31),
            Dispatch = r.IsNull(32) ? null : r.Text(32),
            Session = r.IsNull(33) ? null : r.Text(33)
        }, new SqlArg("$step", stepId));
        if (row is null) return null;
        var start = JsonSerializer.Deserialize(row.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        var key = start.StepIds.SingleOrDefault(p => p.Value == stepId).Key;
        if (key is null || !start.Snapshot.Definition.Steps.TryGetValue(key, out var spec)) return null;
        var resume = row.Session is null ? null : SessionTransitions.ReadWorkflowResume(scope, stepId);
        var selectedProvider = resume is null ? spec.Provider ?? "" : resume.Provider;
        var selectedModel = resume is null ? spec.Model : resume.Model;
        var account = accountContext?.Invoke(selectedProvider) ?? "";
        var provider = scope.QueryFirst("""
            SELECT installation,auth_state,facts_json IS NOT NULL AND ($model IS NULL OR EXISTS(
              SELECT 1 FROM json_each(facts_json,'$.models') model WHERE json_extract(model.value,'$.id')=$model))
            FROM engine_provider WHERE machine=$machine AND provider=$provider AND account_context=$account
            """, r => new DispatchProvider(configured?.Invoke(selectedProvider, account) == true, r.Text(0), r.Text(1), r.Long(2) != 0),
            new SqlArg("$machine", row.Machine), new("$provider", selectedProvider), new("$account", account), new("$model", selectedModel))
            ?? new DispatchProvider(configured?.Invoke(selectedProvider, account) == true, null, null, false);
        var dependencies = scope.Query("""
            SELECT dep.id,d.optional,dep.rule_skippable,dep.version,dep.state,dep.generation,dep.attempt,receipt.hash,receipt.observed_head,dep.outcome_reason
            FROM engine_step_dependency d JOIN engine_step dep ON dep.id=d.dependency_id
            LEFT JOIN engine_workflow_receipt receipt ON receipt.id=dep.receipt_id AND receipt.state='accepted'
            WHERE d.step_id=$step ORDER BY dep.id COLLATE BINARY
            """, r => new DispatchDependency(r.Text(0), r.Long(1) != 0, r.Long(2) != 0, r.Long(3), r.Text(4), (int)r.Long(5), (int)r.Long(6),
                r.IsNull(7) ? null : r.Text(7), r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9)), new SqlArg("$step", stepId)).ToImmutableArray();
        var children = scope.Query("SELECT id,state,epoch,cancel_requested FROM engine_session WHERE parent_id=$owner AND state<>'ended' ORDER BY id COLLATE BINARY",
            r => new DispatchChild(r.Text(0), r.Text(1), r.Long(2), r.Long(3) != 0), new SqlArg("$owner", row.Owner)).ToImmutableArray();
        var ancestors = scope.Query("""
            WITH RECURSIVE ancestors(id,parent_id,depth) AS (
              SELECT id,parent_id,0 FROM engine_session WHERE id=$owner
              UNION ALL SELECT p.id,p.parent_id,a.depth+1 FROM engine_session p JOIN ancestors a ON p.id=a.parent_id WHERE a.depth<$depth)
            SELECT id,parent_id FROM ancestors ORDER BY depth DESC
            """, r => new DispatchAncestor(r.Text(0), r.IsNull(1) ? null : r.Text(1)), new SqlArg("$owner", row.Owner), new("$depth", depthLimit)).ToImmutableArray();
        var reservations = scope.Query("""
            SELECT run.id,st.id,st.generation,st.attempt,st.session_id,st.authority_machine,st.authority_epoch,st.state
            FROM engine_step st JOIN engine_run run ON run.id=st.run_id
            WHERE run.machine=$machine AND run.definition_hash IS NOT NULL AND st.kind IN ('agent','review')
              AND st.session_id IS NOT NULL AND (st.state IN ('running','waiting') OR EXISTS (
                SELECT 1 FROM engine_session worker WHERE worker.id=st.session_id AND worker.state<>'ended'))
            ORDER BY run.id COLLATE BINARY,st.id COLLATE BINARY,st.generation,st.attempt
            """, r => new DispatchReservation(r.Text(0), r.Text(1), (int)r.Long(2), (int)r.Long(3), r.IsNull(4) ? null : r.Text(4),
                r.IsNull(5) ? null : r.Text(5), r.IsNull(6) ? (long?)null : r.Long(6), r.Text(7)), new SqlArg("$machine", row.Machine)).ToImmutableArray();
        var shared = scope.QueryFirst("""
            SELECT MIN(max_sessions) FROM engine_run WHERE machine=$machine AND definition_hash IS NOT NULL
            AND state IN ('queued','running','needs_me','cancelling')
            """, r => r.IsNull(0) ? 0 : r.Long(0), new SqlArg("$machine", row.Machine));
        var redo = ReviewTransitions.RedoValues(scope, row.Run, stepId, row.Generation);
        var sourceDigest = redo is null && resume is null
            ? DispatchIdentity.Hash("workflow-dispatch-source/v1", row.Snapshot)
            : DispatchIdentity.Hash("workflow-dispatch-source/v1", row.Snapshot, redo?.SourceDigest,
                selectedProvider, selectedModel, row.Folder, resume?.NativeId, resume?.AccountContext);
        var witness = new DispatchEligibility(row.Machine, row.Run, row.RunMachine, row.RunVersion, row.RunState, stepId, row.StepVersion,
            row.StepState, row.Generation, row.Attempt, checked(row.Attempt + 1), checked(row.InGeneration + 1), row.Hash, row.Owner, row.OwnerEpoch,
            row.OwnerState, row.Activity, row.Cancelled, row.AuthorityMachine, row.AuthoritySession, row.AuthorityEpoch, row.Workspace,
            row.WorkspaceVersion, row.WorkspaceState, row.Folder, row.Base, row.Head, sourceDigest,
            selectedProvider, selectedModel, spec.Permission, account, provider, depthLimit, childLimit, row.RunLimit, shared, ancestors, children, dependencies, reservations, resume);
        var reason = row.RunMachine != row.Machine ? "wrong_machine" : row.Paused ? "run_paused" : row.Setup != "ready" ? "setup_not_ready" :
            row.RunState is not ("queued" or "running" or "needs_me") ? "run_not_ready" : row.Reserved || row.Holder != "local" || row.Cancelled ||
            row.OwnerState is not ("idle" or "running" or "needs_me") ? "owner_reserved" :
            row.StepState != "pending" || row.Dispatch == "redo_pending" || row.Kind is not ("agent" or "review") || row.Session is null && row.Kind != "review" && row.AuthoritySession is not null ? "step_not_ready" :
            row.Session is not null && resume is null ? "workflow_resume_unavailable" :
            resume is not null && (resume.AccountContext != account || resume.Folder != row.Folder || resume.WorkspaceId != row.Workspace || resume.Head != row.Head) ? "workflow_resume_context_changed" :
            row.WorkspaceState != "ready" || row.Folder == "" || row.Head is null ? "workspace_not_ready" :
            !provider.Configured || provider.Installation != "installed" || selectedModel is null && !provider.ModelAvailable ? "provider_unavailable" :
            provider.Authentication is not ("signed_in" or "unverified" or "unsupported") ? "sign_in_required" :
            !provider.ModelAvailable ? "model_unavailable" :
            dependencies.Any(d => !(d.State == "succeeded" && d.ReceiptHash is not null || d.State == "skipped" && d.RuleSkippable && d.RuleReason is not null || d.Optional && (d.State is "failed" or "cancelled"))) ? "dependency_not_ready" :
            children.Length >= childLimit ? "child_limit" : ancestors.Length > depthLimit || ancestors.Length == 0 || ancestors[0].ParentId is not null ? "depth_limit" :
            reservations.Length >= shared || reservations.Count(r => r.RunId == row.Run) >= row.RunLimit ? "workflow_limit" : null;
        var json = JsonSerializer.Serialize(witness, DispatchJson.Default.DispatchEligibility);
        return new(witness, DispatchIdentity.Hash("workflow-dispatch-eligibility/v1", json), reason is null, reason);
    }
}
