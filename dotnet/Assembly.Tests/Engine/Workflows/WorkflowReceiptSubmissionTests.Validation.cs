using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Theory]
    [InlineData("missing", false)]
    [InlineData("wrong_head", false)]
    [InlineData("invented_reason", false)]
    [InlineData("owner_review", true)]
    [InlineData("wrong_tests", false)]
    [InlineData("tests_verified", true)]
    public void Build_acceptance_requires_bound_resolved_tests_or_original_owner_review_reason(string mode, bool accepted)
    {
        using var f = new Fixture();
        f.Core.Db.Writer.Write("test.build-kind", s => s.Execute("UPDATE engine_step SET result='build' WHERE id='step'"));
        var tests = mode is "wrong_tests" or "tests_verified" ? new[] { "ReceiptCheckTests.Build_checks_actual_new_commits_and_requires_complete_list" } : [];
        var body = JsonSerializer.Serialize(new { commits = new[] { new string('a', 40) }, tests_added = tests, summary = "Only documentation changed; manual owner review is sufficient." });
        var submitted = f.Prepare(body: body); Assert.True(f.Apply(submitted).Applied);
        ReceiptBuildVerification? verification = mode switch
        {
            "missing" => null,
            "wrong_head" => new("owner_review", new string('c', 40), tests, "Only documentation changed"),
            "invented_reason" => new("owner_review", new string('a', 40), tests, "Imaginary reason"),
            "wrong_tests" => new("tests_verified", new string('a', 40), ["Fabricated.Test"], null),
            "tests_verified" => new("tests_verified", new string('a', 40), tests, null),
            _ => new("owner_review", new string('a', 40), tests, "Only documentation changed")
        };
        var result = Confirm(f, submitted, verification);
        Assert.Equal(accepted, result.Applied);
        Assert.Equal(accepted ? "accepted" : "pending", f.Core.Text("SELECT state FROM engine_workflow_receipt"));
        if (!accepted) Assert.Equal("bad_receipt", result.Error);
        else Assert.Equal(mode, f.Core.Text("SELECT json_extract(safe_body_json,'$.buildVerification.result') FROM engine_workflow_history WHERE kind='receipt_checked'"));
    }

    [Fact]
    public void Accepted_failed_review_records_findings_and_redo_before_any_terminal_failure()
    {
        using var f = new Fixture(); f.Core.SeedStep("build", state: "succeeded");
        var snapshot = WorkflowParser.Parse(new("""
            name: Review
            steps:
              build:
                agent: {provider: codex}
                prompt: prompt.md
                result: build
              review:
                needs: [build]
                result: review
                review: {of: build, agent: {provider: codex}, prompt: prompt.md}
            """, ImmutableSortedDictionary<string, string>.Empty.Add("prompt.md", "Review the current checkout")));
        var request = new WorkflowStartRequest(Guid.NewGuid(), "r", 0, 0, "owner", "review", "workflow.yml", "project", "Review", null, JsonSerializer.SerializeToElement(new { }));
        var source = new WorkflowStartBody(new(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "fixture"), "r", 0, 0, 1000),
            request, "project", "/tmp/receipt-fixture", snapshot, new(request.Inputs, "inputs"),
            ImmutableSortedDictionary<string, string>.Empty.Add("build", "build").Add("review", "step"), "request");
        f.Core.Db.Writer.Write("test.review-snapshot", s => s.Execute("""
            UPDATE engine_step SET kind='review',result='review',definition_key='review' WHERE id='step';
            UPDATE engine_step SET definition_key='build' WHERE id='build';
            INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES('step','build',0);
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('snapshot',1,$machine,1000,1000,'r',1,'definition','inputs',$body);
            """, new("$machine", f.Core.Machine), new("$body", JsonSerializer.Serialize(source, WorkflowPreparationJson.Default.WorkflowStartBody))));
        var review = """{"verdict":"fail","head":"HEAD","findings":[{"severity":"blocking","late":false,"path_line":"src.cs:1","text":"Race","fix":"Fence"}]}""".Replace("HEAD", new string('a', 40));
        var submitted = f.Prepare(body: review, state: "failed"); Assert.True(f.Apply(submitted).Applied);
        var result = Confirm(f, submitted); Assert.True(result.Applied, result.Error);
        Assert.Equal("running", f.Core.State("engine_run", "r"));
        Assert.Equal("waiting", f.Core.State("engine_step", "step"));
        Assert.Equal("review_settled", f.Core.Text("SELECT dispatch_state FROM engine_step WHERE id='step'"));
        Assert.Equal("redo_pending", f.Core.Text("SELECT dispatch_state FROM engine_step WHERE id='build'"));
        Assert.Equal("ended", f.Core.State("engine_session", "worker"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_requested'"));
        Assert.Equal("Race", f.Core.Text("SELECT json_extract(body_json,'$.report.receipt.findings[0].text') FROM engine_workflow_receipt WHERE state='accepted'"));
    }

    private static CommandResult Confirm(Fixture f, PreparedWorkflowCommand submitted, ReceiptBuildVerification? build = null)
    {
        var id = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_receipt'")!;
        var claim = f.Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "worker")); Assert.True(claim.Applied, claim.Error);
        var effect = EffectTransitions.Read(f.Core.Db, id, default)!; var original = submitted.Receipt!;
        var receipt = WorkflowPreparation.ReceiptConfirmation(effect, new(original.ReceiptId, 1, 0, true, original.Capture.Head, null),
            original.Capture.Fingerprint, [], [new("https://evidence.invalid/proof", "reachable", 1000, null, null, original.Capture.Head)],
            default, original.Capture, buildVerification: build);
        return f.Core.Apply(new CompleteEffect(receipt), id, version: claim.Version, actor: new(ActorKind.System, "worker"));
    }
}
