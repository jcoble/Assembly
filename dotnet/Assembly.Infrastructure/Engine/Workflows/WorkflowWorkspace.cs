using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Workspaces;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>Bounded Git allocation. Its only inputs are the already committed exact allocation.</summary>
public sealed class WorkflowWorkspace(string managedRoot, string proofRoot, IGit? git = null) : IWorkflowWorkspace
{
    private readonly IGit _git = git ?? new GitCli();

    public async Task<EffectReceipt> PrepareAsync(Effect effect, bool reconcile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intent = effect.Payload.Deserialize(EngineJson.Default.WorkspaceIntent);
        if (effect.Kind != "workflow_workspace" || intent is null || effect.RunId != intent.RunId)
            throw new CallFailed("invalid_workspace_effect");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(60));
        WorkspacePrepared result;
        try
        {
            Validate(intent);
            var actual = await Scalar(intent.Repository, ["rev-parse", "--verify", intent.BaseHead + "^{commit}"], stop.Token);
            if (actual != intent.BaseHead) throw new CallFailed("workspace_base_unavailable");
            await OwnProof(intent, stop.Token);
            if (!intent.Owned)
            {
                var head = await Scalar(intent.Repository, ["rev-parse", "HEAD"], stop.Token);
                if (head != intent.BaseHead) throw new CallFailed("workspace_parent_head_changed");
                result = Done(intent, head);
            }
            else
            {
                var marker = Path.Combine(Path.GetDirectoryName(intent.Path)!, ".allocations", intent.AllocationId + ".json");
                var existed = File.Exists(marker);
                if (existed)
                {
                    if (await File.ReadAllTextAsync(marker, stop.Token) != JsonSerializer.Serialize(intent, EngineJson.Default.WorkspaceIntent))
                        throw new CallFailed("workspace_allocation_conflict");
                }
                else
                {
                    if (Directory.Exists(intent.Path) || File.Exists(intent.Path)) throw new CallFailed("workspace_path_conflict");
                    await Marker(marker, JsonSerializer.Serialize(intent, EngineJson.Default.WorkspaceIntent), stop.Token);
                }
                var trees = await Trees(intent.Repository, stop.Token);
                var tree = trees.SingleOrDefault(t => t.Path == intent.Path);
                if (tree is null)
                {
                    if (Directory.Exists(intent.Path) || File.Exists(intent.Path) ||
                        !string.IsNullOrEmpty(await Scalar(intent.Repository, ["for-each-ref", "--format=%(objectname)", "refs/heads/" + intent.Branch], stop.Token)))
                        throw new CallFailed("workspace_partial_allocation");
                    await Records(intent.Repository, ["worktree", "add", "-b", intent.Branch, "--", intent.Path, intent.BaseHead], stop.Token);
                    tree = (await Trees(intent.Repository, stop.Token)).SingleOrDefault(t => t.Path == intent.Path);
                }
                if (tree is null || tree.Branch != intent.Branch || tree.Head != intent.BaseHead || tree.IsBare || tree.Prunable)
                    throw new CallFailed("workspace_allocation_conflict");
                result = Done(intent, tree.Head);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { result = Failed(intent, "workspace_setup_timeout"); }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or WorkflowFieldException)
        { result = Failed(intent, error.Message); }
        return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(result, EngineJson.Default.WorkspacePrepared), null);
    }

    private void Validate(WorkspaceIntent i)
    {
        if (!Guid.TryParseExact(i.RunId, "N", out _) || !Guid.TryParseExact(i.AllocationId, "N", out _) ||
            i.StepId is not null && !Guid.TryParseExact(i.StepId, "N", out _) || i.Generation < 0 ||
            i.BaseHead.Length is not (40 or 64) || !i.BaseHead.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Path.IsPathFullyQualified(i.Repository) || WorkflowPaths.Canonical(i.Repository) != i.Repository ||
            WorkflowPaths.Canonical(i.Path) != i.Path || WorkflowPaths.Canonical(i.ProofDir) != i.ProofDir)
            throw new CallFailed("invalid_workspace_allocation");
        if (Path.GetDirectoryName(i.ProofDir) != WorkflowPaths.Canonical(proofRoot) || !Guid.TryParseExact(Path.GetFileName(i.ProofDir), "N", out _) || Inside(i.Repository, i.ProofDir) ||
            Inside(i.Repository, WorkflowPaths.Canonical(managedRoot)) || Inside(i.ProofDir, i.Path) || Inside(i.Path, i.ProofDir))
            throw new CallFailed("invalid_workspace_roots");
        if (i.Owned)
        {
            if (!Inside(WorkflowPaths.Canonical(managedRoot), i.Path) || i.Branch.Length == 0 || i.Branch[0] == '-' ||
                i.Branch.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new CallFailed("invalid_workspace_allocation");
        }
        else if (i.Path != i.Repository) throw new CallFailed("invalid_parent_workspace");
    }

    private static bool Inside(string root, string path) => path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static WorkspacePrepared Done(WorkspaceIntent i, string head) => new(i.AllocationId, i.Path, i.Branch, i.BaseHead, head, null);
    private static WorkspacePrepared Failed(WorkspaceIntent i, string error) => new(i.AllocationId, i.Path, i.Branch, i.BaseHead, "", error);

    private static async Task OwnProof(WorkspaceIntent i, CancellationToken ct)
    {
        var marker = Path.Combine(i.ProofDir, ".assembly-workflow-allocation");
        // Proof belongs to the run, so every step checks the same marker rather than claiming it anew.
        var value = i.RunId;
        if (Directory.Exists(i.ProofDir))
        {
            if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != value)
                throw new CallFailed("workflow_proof_conflict");
            return;
        }
        Directory.CreateDirectory(i.ProofDir);
        await Marker(marker, value, ct);
    }

    private static async Task Marker(string path, string value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(temp, value, ct);
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<List<string>> Records(string folder, string[] args, CancellationToken ct)
    {
        List<string> values = [];
        await foreach (var value in _git.ReadAsync(folder, args, null, ct)) values.Add(value);
        return values;
    }
    private async Task<string> Scalar(string folder, string[] args, CancellationToken ct) => string.Concat(await Records(folder, args, ct)).TrimEnd('\r', '\n');
    private async Task<IReadOnlyList<Worktree>> Trees(string folder, CancellationToken ct) =>
        WorktreePorcelain.Parse(string.Join('\0', await Records(folder, ["worktree", "list", "--porcelain", "-z"], ct)));
}
