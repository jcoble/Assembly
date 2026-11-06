using System.Text.Json;
using Assembly.Application.Common.Git;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Infrastructure.Engine.Workflows;

// Internal host inputs are resolved from the committed attempt, never deserialized from report arguments.
internal sealed record ReceiptCheckContext(string Workspace, string ProofDir, string AttemptBaseHead,
    string FrozenHead, string StartFingerprint, string ReportFingerprint,
    IReadOnlyList<string> EarlierFindingTexts, IReadOnlyList<string> ExpectedFiles);
internal sealed record ReceiptObservation(string? Error, string? Head, string? Fingerprint,
    IReadOnlyList<CheckedReceiptFile> Files);

/// <summary>Contextual local checks. Authenticated document/evidence checks remain a required host dependency.</summary>
internal sealed partial class ReceiptChecks(IGit? git = null)
{
    private readonly ReceiptCheckout _checkout = new(git);

    internal async Task<ReceiptObservation> CheckLocalAsync(ReceiptCheckContext context, string kind,
        JsonElement body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shape = ReceiptValidation.Shape(kind, body);
        if (shape is not null) return Failed(shape);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var before = await _checkout.Capture(context.Workspace, stop.Token);
            if (before.Head != context.FrozenHead || before.Fingerprint != context.ReportFingerprint)
                return Failed("changed_code", before);
            if (kind != "build" && before.Fingerprint != context.StartFingerprint)
                return Failed("changed_code", before);
            string? error = kind switch
            {
                "build" => await Build(context, body.Deserialize(ReceiptJson.Default.BuildReceipt)!, before, stop.Token),
                "recon" => await Recon(context, body.Deserialize(ReceiptJson.Default.ReconReceipt)!, stop.Token),
                "spec" => await Spec(context, body.Deserialize(ReceiptJson.Default.SpecReceipt)!, stop.Token),
                "review" => await Review(context, body.Deserialize(ReceiptJson.Default.ReviewReceipt)!, stop.Token),
                "proof" => await Proof(context, body.Deserialize(ReceiptJson.Default.ProofReceipt)!, stop.Token),
                "plan" => null,
                _ => "kind"
            };
            if (error is not null) return Failed(error, before);
            var files = await ReceiptFiles.Expand(context.Workspace, context.ProofDir, context.ExpectedFiles, stop.Token);
            // Detect a report followed by a checkout change while local checks were running.
            var after = await _checkout.Capture(context.Workspace, stop.Token);
            if (before.Fingerprint != after.Fingerprint) return Failed("changed_code", after);
            return new(null, after.Head, after.Fingerprint, files);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("receipt_check_timeout"); }
        catch (WorkflowFieldException error) { return Failed(error.Field); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Assembly.Application.Common.Calls.CallFailed)
        { return Failed(error.Message); }
    }

    private async Task<string?> Build(ReceiptCheckContext context, BuildReceipt body, ReceiptCheckoutState observed, CancellationToken ct)
    {
        if (!observed.Clean) return "changed_code";
        var made = await _checkout.FirstParentCommits(context.Workspace, context.AttemptBaseHead, observed.Head, ct);
        var ancestors = await _checkout.Ancestors(context.Workspace, observed.Head, ct);
        var original = await _checkout.Ancestors(context.Workspace, context.AttemptBaseHead, ct);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in body.Commits)
        {
            var resolved = await _checkout.ResolveCommit(context.Workspace, commit, ct);
            if (resolved is null || !ancestors.Contains(resolved) || original.Contains(resolved) || !reported.Add(resolved)) return "commits";
        }
        if (made.Any(commit => !reported.Contains(commit))) return "commits";
        // Actual test names and low-impact reasons are resolved by the trusted host evidence producer.
        // File containment alone cannot establish that a name identifies a real test.
        return null;
    }

    private static async Task<string?> Recon(ReceiptCheckContext context, ReconReceipt body, CancellationToken ct)
    {
        await Lines(context.Workspace, "existing", body.Existing, ct);
        await Lines(context.Workspace, "touches", body.Touches, ct);
        return null;
    }
    private static async Task<string?> Spec(ReceiptCheckContext context, SpecReceipt body, CancellationToken ct)
    {
        await Lines(context.Workspace, "reuse", body.Reuse, ct);
        return null;
    }
    private static async Task<string?> Review(ReceiptCheckContext context, ReviewReceipt body, CancellationToken ct)
    {
        if (!string.Equals(body.Head, context.FrozenHead, StringComparison.OrdinalIgnoreCase)) return "head";
        foreach (var finding in body.Findings)
        {
            foreach (var grade in finding.GradesOfEarlier)
                if (!context.EarlierFindingTexts.Contains(grade.Text, StringComparer.Ordinal)) return "grades_of_earlier";
            // Canonical Notion findings are checked through the authenticated host evidence producer, not as files.
            if (Uri.TryCreate(finding.PathLine, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                continue;
            await Lines(context.Workspace, "path_line", [finding.PathLine], ct);
        }
        return null;
    }
    private static async Task<string?> Proof(ReceiptCheckContext context, ProofReceipt body, CancellationToken ct)
    {
        if (!string.Equals(body.Head, context.FrozenHead, StringComparison.OrdinalIgnoreCase)) return "proof_head_mismatch";
        foreach (var file in body.Files)
        {
            try { _ = await ReceiptFiles.Observe(context.ProofDir, file, "proof", "files", ct); }
            catch (WorkflowFieldException) { return "proof_file_missing"; }
        }
        // A local pass only establishes file/head consistency; the final effect still requires authenticated links/check receipts.
        return null;
    }

    private static async Task Lines(string root, string field, IReadOnlyList<string> entries, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var split = entry.LastIndexOf(':');
            if (split <= 0 || !int.TryParse(entry.AsSpan(split + 1), out var line) || line < 1)
                throw new WorkflowFieldException(field, "expected existing path:line");
            var file = ReceiptFiles.Contained(root, entry[..split], field);
            using var reader = new StreamReader(file, new System.Text.UTF8Encoding(false, true));
            for (var i = 0; i < line; i++)
                if (await reader.ReadLineAsync(ct) is null) throw new WorkflowFieldException(field, "line does not exist");
        }
    }
    private static ReceiptObservation Failed(string error, ReceiptCheckoutState? state = null) => new(error, state?.Head, state?.Fingerprint, []);
}
