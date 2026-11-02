using System.Security.Cryptography;
using System.Text;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Infrastructure.Engine.Workflows;

public static class WorkflowPrBodyFile
{
    public static async Task WriteAsync(WorkflowPrBody body, string proofRoot, string runId, CancellationToken ct)
    {
        await ValidatePath(body, proofRoot, runId, ct);
        if (CommandStepTransitions.Digest(body.Text) != body.Sha256) throw new InvalidOperationException("pr_body_digest_mismatch");
        var temporary = body.Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(body.Text), ct); await file.FlushAsync(ct); file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, body.Path, true);
        }
        finally { File.Delete(temporary); }
    }

    public static async Task<bool> VerifyAsync(WorkflowPrBody body, string proofRoot, string runId, CancellationToken ct)
    {
        await ValidatePath(body, proofRoot, runId, ct);
        await using var file = new FileStream(body.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return file.Length <= 240000 && Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) == body.Sha256;
    }

    private static async Task ValidatePath(WorkflowPrBody body, string proofRoot, string runId, CancellationToken ct)
    {
        var root = Path.GetFullPath(proofRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(body.Path) || !Path.GetFullPath(body.Path).StartsWith(root, StringComparison.Ordinal) ||
            Path.GetFileName(body.Path) != "pr-body.md") throw new InvalidOperationException("invalid_pr_body_path");
        for (var parent = Path.GetDirectoryName(body.Path); parent is not null && parent.Length >= root.Length - 1; parent = Path.GetDirectoryName(parent))
            if (new DirectoryInfo(parent).LinkTarget is not null) throw new InvalidOperationException("invalid_pr_body_path");
        var marker = Path.Combine(Path.GetDirectoryName(body.Path)!, ".assembly-workflow-allocation");
        if (new FileInfo(body.Path).LinkTarget is not null || new FileInfo(marker).LinkTarget is not null ||
            !File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != runId) throw new InvalidOperationException("workflow_proof_conflict");
    }
}
