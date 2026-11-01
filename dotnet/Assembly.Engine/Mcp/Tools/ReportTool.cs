using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Prs;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Mcp.Tools;

public sealed record ReportArguments(string? State = null, string? Summary = null,
    IReadOnlyList<string>? ProofUrls = null, ReportPrArguments? Pr = null, string? RunId = null);
public sealed record ReportPrArguments(string? Url = null, string? Head = null, string? State = null);
public sealed record ReportAnswer(string? StepId, string? PrId);

public sealed class ReportTool(McpGate gate)
    : EngineTool("report", "Report the result and proof for your assigned workflow step, optionally its PR. A run owner may report a PR on its own run using runId, but cannot report a step result. Only observed repository state establishes a merged PR.", """
        {"type":"object","additionalProperties":false,"anyOf":[{"required":["state"]},{"required":["pr"]}],"properties":{
            "state":{"type":"string","enum":["succeeded","failed"]},
            "summary":{"type":"string","minLength":1,"maxLength":600},
            "proofUrls":{"type":"array","maxItems":20,"items":{"type":"string","format":"uri"}},
            "runId":{"type":"string","minLength":1},
            "pr":{"type":"object","additionalProperties":false,"required":["url","head","state"],"properties":{
                "url":{"type":"string","format":"uri"},"head":{"type":"string","pattern":"^[a-fA-F0-9]{7,40}$"},
                "state":{"type":"string","enum":["open","merged","closed"]}}}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
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
            string? prId = null;
            if (args.Pr is not null)
            {
                prId = write.Scope.QueryFirst("SELECT id FROM engine_pr WHERE repo=$repo AND number=$number",
                    r => r.Text(0), new("$repo", parsed!.Value.Repo), new("$number", parsed.Value.Number));
                if (run.Pr is not null && run.Pr != prId) throw new McpRefused(new("run_has_pr"));
                prId ??= Guid.NewGuid().ToString();
                // PR authority is the live assignment; finish the step only after recording its claim.
                write.Apply(prId, write.VersionOf("pr", prId), write.EpochOf(run.Owner),
                    new ReportPr(runId!, parsed!.Value.Repo, parsed.Value.Number, args.Pr.Head!, args.Pr.Url!, args.Pr.State!));
            }
            if (args.State is not null)
                write.Apply(stepId!, write.VersionOf("step", stepId!), row.Epoch,
                    new SetStepState(args.State, attempt!.Value, args.ProofUrls ?? [], args.Summary, false));
            return new ReportAnswer(stepId, prId);
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.ReportAnswer));
    }

    private static bool Https(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Length > 0;

    private static void ValidateShape(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("state" or "summary" or "proofUrls" or "pr" or "runId"))
                throw Bad(property.Name, "is not a supported argument");
            if (property.Name == "proofUrls")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                    throw Bad(property.Name, "must be an array of strings");
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
