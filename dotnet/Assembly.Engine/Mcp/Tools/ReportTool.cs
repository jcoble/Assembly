using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Prs;
using Assembly.Engine.Runs;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Engine.Mcp.Tools;

public sealed record ReportArguments(string? State = null, string? Summary = null,
    IReadOnlyList<string>? ProofUrls = null, ReportPrArguments? Pr = null, string? RunId = null, JsonElement? Receipt = null);
public sealed record ReportPrArguments(string? Url = null, string? Head = null, string? State = null);
public sealed record ReportAnswer(string? StepId, string? PrId, string? ReceiptId = null, string? State = null);

public sealed class ReportTool(McpGate gate, Func<McpCaller, CancellationToken, Task<ReceiptReportCapture?>>? captureReceipt = null)
    : EngineTool("report", "Report the result and proof for your assigned workflow step, optionally its PR. A run owner may report a PR on its own run using runId, but cannot report a step result. Workflow success returns verifying until trusted receipt checks finish. Only observed repository state establishes a merged PR.", """
        {"type":"object","additionalProperties":false,"anyOf":[{"required":["state"]},{"required":["pr"]}],"properties":{
            "state":{"type":"string","enum":["succeeded","failed"]},
            "summary":{"type":"string","minLength":1,"maxLength":600},
            "proofUrls":{"type":"array","maxItems":20,"items":{"type":"string","format":"uri"}},
            "runId":{"type":"string","minLength":1},
            "receipt":{"type":"object"},
            "pr":{"type":"object","additionalProperties":false,"required":["url","head","state"],"properties":{
                "url":{"type":"string","format":"uri"},"head":{"type":"string","pattern":"^[a-fA-F0-9]{7,40}$"},
                "state":{"type":"string","enum":["open","merged","closed"]}}}}}
        """.Replace("\"receipt\":{\"type\":\"object\"}", "\"receipt\":" + WorkflowReceiptSchema.Json, StringComparison.Ordinal))
{
    protected override async ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        ValidateShape(arguments);
        var args = arguments.Deserialize(McpJson.Default.ReportArguments)!;
        if (args.State is null && args.Pr is null) throw Bad("arguments", "needs state or pr");
        if (args.State is not null && args.State is not ("succeeded" or "failed"))
            throw Bad("state", "must be succeeded or failed");
        if (args.Summary is not null && (string.IsNullOrWhiteSpace(args.Summary) || args.Summary.Length > 600) ||
            args.State is not null && args.Summary is null)
            throw Bad("summary", "must contain 1 to 600 characters");
        if (args.ProofUrls is not null && (args.ProofUrls.Count > 20 || args.ProofUrls.Any(url => !Https(url))))
            throw Bad("proofUrls", "must contain at most 20 https URLs");
        if (args.RunId is not null && string.IsNullOrWhiteSpace(args.RunId)) throw Bad("runId", "must name a run");
        var parsed = args.Pr is null ? null : GitHubPrUrl.Parse(args.Pr.Url!);
        if (args.Pr is not null)
        {
            if (parsed is null) throw Bad("pr.url", "must be a GitHub pull request URL");
            if (args.Pr.Head is not { Length: >= 7 and <= 40 } || !args.Pr.Head.All(char.IsAsciiHexDigit))
                throw Bad("pr.head", "must be 7 to 40 hexadecimal characters");
            if (args.Pr.State is not ("open" or "merged" or "closed")) throw Bad("pr.state", "must be open, merged or closed");
        }
        if (args.Receipt is not null && args.State is null) throw Bad("state", "is required with receipt");
        PreparedWorkflowCommand? prepared = null;
        if (args.State == "succeeded" || args.Receipt is not null)
        {
            var original = gate.ReadReport(caller, arguments, (row, scope) =>
            {
                var assignment = FindAssignment(row, scope);
                if (assignment?.Kind is null) return (Assignment: assignment, Stored: (ReceiptBody?)null);
                var json = scope.QueryFirst("""
                    SELECT body_json FROM engine_workflow_receipt WHERE step_id=$step AND attempt=$attempt
                      AND (state IN ('pending','accepted') OR hash=$hash) ORDER BY created_ms,id LIMIT 1
                    """, r => r.Text(0), new SqlArg("$step", assignment.Id), new("$attempt", assignment.Attempt),
                    new("$hash", args.Receipt is { } receipt ? WorkflowPreparation.ReceiptHash(receipt, cancellationToken) : ""));
                var stored = json is null ? null : JsonSerializer.Deserialize(json, ReceiptPreparationJson.Default.ReceiptBody);
                return (Assignment: assignment, Stored: stored);
            }, cancellationToken);
            if (original.Assignment?.Kind is not null)
            {
                if (args.Receipt is null) throw new McpRefused(new("receipt_required"));
                if (original.Stored is { } stored)
                {
                    if (stored.Capture.SessionId != caller.SessionId || stored.Capture.Epoch != caller.Epoch ||
                        stored.Capture.StepId != original.Assignment.Id || stored.Report.Attempt != original.Assignment.Attempt)
                        throw new McpRefused(new("receipt_unknown"));
                    var candidate = WorkflowPreparation.Receipt(stored.Context,
                        new(stored.Report.Attempt, stored.Report.Generation, args.State!, args.Summary!, args.ProofUrls ?? [], args.Receipt),
                        stored.Capture, cancellationToken);
                    if (candidate.Receipt!.ReportHash != stored.ReportHash) throw new McpRefused(new("report_conflict"));
                    prepared = WorkflowPreparation.Receipt(stored.Context, stored.Report, stored.Capture, cancellationToken, replay: true);
                }
                else
                {
                    var capture = captureReceipt is null ? null : await captureReceipt(caller, cancellationToken);
                    if (capture is null) throw new McpRefused(new("receipt_capture_unavailable"));
                    prepared = gate.Read(caller, (row, scope) =>
                    {
                        var assignment = FindAssignment(row, scope);
                        if (assignment?.Id != capture.StepId || row.SessionId != capture.SessionId || row.Epoch != capture.Epoch || assignment.Attempt != capture.Attempt)
                            throw new McpRefused(new("wrong_attempt"));
                        var version = scope.QueryFirst("SELECT version FROM engine_step WHERE id=$id", r => r.Long(0), new SqlArg("$id", capture.StepId));
                        var context = new CommandContext(Guid.NewGuid(), gate.Machine, new(ActorKind.Session, row.SessionId), capture.StepId, version, row.Epoch, 0);
                        return WorkflowPreparation.Receipt(context,
                            new(capture.Attempt, capture.Generation, args.State!, args.Summary!, args.ProofUrls ?? [], args.Receipt), capture, cancellationToken);
                    }, cancellationToken);
                }
            }
        }
        if (prepared?.ReceiptReplay == true)
        {
            gate.ReplayReceipt(caller, arguments, prepared);
            var replayAnswer = gate.ReadReport(caller, arguments, (_, scope) =>
            {
                var receipt = scope.QueryFirst("SELECT id,state FROM engine_workflow_receipt WHERE id=$id",
                    r => (Id: r.Text(0), State: r.Text(1)), new SqlArg("$id", prepared.Receipt!.ReceiptId));
                return new ReportAnswer(prepared.Receipt!.Capture.StepId, null, receipt.Id, receipt.State == "pending" ? "verifying" : receipt.State);
            }, cancellationToken);
            return JsonSerializer.SerializeToElement(replayAnswer, McpJson.Default.ReportAnswer);
        }
        var answer = gate.Write(caller, write =>
        {
            var row = write.Caller;
            var stepId = row.StepId;
            var runId = row.RunId;
            var attempt = row.Attempt;
            if (stepId is null && args.State is not null && row.RunId is not null)
            {
                // A retry may reach only one exact assignment in the caller's current run; never guess the latest step.
                var assigned = write.Scope.Query("""
                    SELECT id,attempt,state FROM engine_step WHERE session_id=$session AND run_id=$run LIMIT 2
                    """, r => new { Id = r.Text(0), Attempt = (int)r.Long(1), State = r.Text(2) },
                    new("$session", row.SessionId), new("$run", row.RunId));
                if (assigned.Count == 1 && assigned[0].State is "succeeded" or "failed") { stepId = assigned[0].Id; attempt = assigned[0].Attempt; }
            }
            var worker = stepId is not null;
            if (!worker && args.State is not null) throw new McpRefused(new("no_step"));
            if (!worker)
            {
                if (args.RunId is null)
                {
                    var ownsRun = write.Scope.QueryFirst("SELECT COUNT(*) FROM engine_run WHERE owner_session_id=$id",
                        r => r.Long(0), new SqlArg("$id", row.SessionId)) > 0;
                    if (ownsRun) throw Bad("runId", "is required for a run's owner");
                    throw new McpRefused(new("no_step"));
                }
                runId = args.RunId;
            }
            var run = write.Scope.QueryFirst("SELECT owner_session_id,pr_id,state FROM engine_run WHERE id=$id",
                r => new { Owner = r.Text(0), Pr = r.IsNull(1) ? null : r.Text(1), State = r.Text(2) }, new SqlArg("$id", runId));
            if (run is null || !worker && (run.Owner != row.SessionId || RunTransitions.IsTerminal(run.State))) throw new McpRefused(new("no_step"));
            string? prId = prepared?.ReceiptReplay == true && args.Pr is not null ? run.Pr : null;
            if (args.Pr is not null && prepared?.ReceiptReplay != true)
            {
                prId = write.Scope.QueryFirst("SELECT id FROM engine_pr WHERE repo=$repo AND number=$number",
                    r => r.Text(0), new("$repo", parsed!.Value.Repo), new("$number", parsed.Value.Number));
                if (run.Pr is not null && run.Pr != prId) throw new McpRefused(new("run_has_pr"));
                prId ??= Guid.NewGuid().ToString();
                // PR authority is the live assignment; finish the step only after recording its claim.
                write.Apply(prId, write.VersionOf("pr", prId), write.EpochOf(run.Owner),
                    new ReportPr(runId!, parsed!.Value.Repo, parsed.Value.Number, args.Pr.Head!, args.Pr.Url!, args.Pr.State!));
            }
            CommandResult? receiptResult = null;
            if (prepared is not null) receiptResult = write.ApplyWorkflowReceipt(prepared);
            else if (args.State is not null)
                write.Apply(stepId!, write.VersionOf("step", stepId!), row.Epoch,
                    new SetStepState(args.State, attempt!.Value, args.ProofUrls ?? [], args.Summary, false));
            if (prepared is not null)
            {
                if (receiptResult?.Late == true) return new ReportAnswer(stepId, prId, null, "late");
                var receipt = write.Scope.QueryFirst("SELECT id,state FROM engine_workflow_receipt WHERE step_id=$step AND attempt=$attempt AND hash=$hash ORDER BY created_ms,id LIMIT 1",
                    r => (Id: r.Text(0), State: r.Text(1)), new SqlArg("$step", stepId), new("$attempt", attempt), new("$hash", prepared.Receipt!.ReportHash));
                return new ReportAnswer(stepId, prId, receipt.Id, receipt.State == "pending" ? "verifying" : receipt.State);
            }
            return new ReportAnswer(stepId, prId);
        });
        return JsonSerializer.SerializeToElement(answer, McpJson.Default.ReportAnswer);
    }

    private sealed record ReportAssignment(string Id, int Attempt, string? Kind);
    private static ReportAssignment? FindAssignment(CallerRow row, IReadScope scope)
    {
        if (row.StepId is not null)
            return scope.QueryFirst("SELECT id,attempt,result FROM engine_step WHERE id=$id",
                r => new ReportAssignment(r.Text(0), (int)r.Long(1), r.IsNull(2) ? null : r.Text(2)), new SqlArg("$id", row.StepId));
        if (row.RunId is null) return null;
        var assignments = scope.Query("SELECT id,attempt,result FROM engine_step WHERE session_id=$session AND run_id=$run LIMIT 2",
            r => new ReportAssignment(r.Text(0), (int)r.Long(1), r.IsNull(2) ? null : r.Text(2)), new SqlArg("$session", row.SessionId), new("$run", row.RunId));
        return assignments.Count == 1 ? assignments[0] : null;
    }

    private static bool Https(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Length > 0;

    private static void ValidateShape(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("state" or "summary" or "proofUrls" or "pr" or "runId" or "receipt"))
                throw Bad(property.Name, "is not a supported argument");
            if (property.Name == "proofUrls")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                    throw Bad(property.Name, "must be an array of strings");
            }
            else if (property.Name == "receipt")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw Bad("receipt", "must be an object");
            }
            else if (property.Name == "pr")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw Bad("pr", "must be an object");
                foreach (var field in property.Value.EnumerateObject())
                    if (field.Name is not ("url" or "head" or "state") || field.Value.ValueKind != JsonValueKind.String)
                        throw Bad("pr." + field.Name, "must be a supported string argument");
            }
            else if (property.Value.ValueKind != JsonValueKind.String) throw Bad(property.Name, "must be a string");
        }
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
