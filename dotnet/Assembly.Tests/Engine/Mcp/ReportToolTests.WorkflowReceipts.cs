using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class ReportToolTests
{
    [Fact]
    public async Task Typed_workflow_report_returns_verifying_then_replays_terminal_without_live_capture()
    {
        await using var h = new Harness(); SeedTypedWorker(h.Fixture);
        var captures = 0;
        var tool = new ReportTool(h.Gate, (_, _) =>
        {
            captures++;
            return Task.FromResult<ReceiptReportCapture?>(new("step", 1, 0, "worker", 7, "ws", 1,
                new string('a', 40), new string('b', 64), "fixture-execution"));
        });
        var first = await h.Call(tool, "worker", TypedReport, 7, record: true); Assert.False(first.IsError);
        using var firstJson = JsonDocument.Parse(Text(first));
        var receiptId = firstJson.RootElement.GetProperty("receiptId").GetString();
        Assert.Equal("verifying", firstJson.RootElement.GetProperty("state").GetString());
        var stored = h.Fixture.Text("SELECT body_json FROM engine_workflow_receipt");
        // Seed a checked terminal row to test tool replay independently of external check execution.
        h.Fixture.Db.Writer.Write("test.checked-terminal", scope => scope.Execute("""
            UPDATE engine_workflow_receipt SET state='accepted';
            UPDATE engine_step SET state='succeeded' WHERE id='step';
            UPDATE engine_session SET state='ended' WHERE id='worker';
            """));
        var duplicate = await h.Call(new ReportTool(h.Gate), "worker", TypedReport.Replace("done", "new outer summary"), 7, record: true);
        Assert.False(duplicate.IsError); using var duplicateJson = JsonDocument.Parse(Text(duplicate));
        Assert.Equal(receiptId, duplicateJson.RootElement.GetProperty("receiptId").GetString());
        Assert.Equal("accepted", duplicateJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_tool_call WHERE session_id='worker' AND step_id='step' AND attempt=1 AND late=1 AND outcome='ok'"));
        Assert.Equal(1, captures); Assert.Equal(stored, h.Fixture.Text("SELECT body_json FROM engine_workflow_receipt"));
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_receipt'"));
    }

    [Fact]
    public async Task Typed_workflow_requires_capture_and_rejects_a_different_pending_receipt()
    {
        await using var h = new Harness(); SeedTypedWorker(h.Fixture);
        Assert.Contains("receipt_required", Text(await h.Call(new ReportTool(h.Gate), "worker", Result, 7)));
        Assert.Contains("receipt_capture_unavailable", Text(await h.Call(new ReportTool(h.Gate), "worker", TypedReport, 7)));
        var tool = new ReportTool(h.Gate, (_, _) => Task.FromResult<ReceiptReportCapture?>(new("step", 1, 0, "worker", 7, "ws", 1,
            new string('a', 40), new string('b', 64), "fixture-execution")));
        Assert.False((await h.Call(tool, "worker", TypedReport, 7)).IsError);
        var conflict = await h.Call(new ReportTool(h.Gate), "worker", TypedReport.Replace("Inspected", "Different"), 7);
        Assert.True(conflict.IsError); Assert.Contains("report_conflict", Text(conflict));
        Assert.Equal(1, h.Fixture.Count("engine_workflow_receipt"));
    }

    [Fact]
    public async Task Typed_failed_review_is_pending_checks_without_failing_required_run()
    {
        await using var h = new Harness(); SeedTypedWorker(h.Fixture);
        h.Fixture.Db.Writer.Write("test.review-kind", scope => scope.Execute("UPDATE engine_step SET kind='review',result='review' WHERE id='step'"));
        var tool = new ReportTool(h.Gate, (_, _) => Task.FromResult<ReceiptReportCapture?>(new("step", 1, 0, "worker", 7, "ws", 1,
            new string('a', 40), new string('b', 64), "fixture-execution")));
        var arguments = """{"state":"failed","summary":"Review findings","proofUrls":["https://drive.google.com/x"],"receipt":{"verdict":"fail","head":"HEAD","findings":[{"severity":"blocking","late":false,"path_line":"src.cs:1","text":"Race","fix":"Fence"}]}}"""
            .Replace("HEAD", new string('a', 40));
        var result = await h.Call(tool, "worker", arguments, 7, record: true);
        Assert.False(result.IsError); Assert.Equal("waiting", h.Fixture.State("engine_step", "step"));
        Assert.Equal("running", h.Fixture.State("engine_run", "r"));
        Assert.Equal("pending", h.Fixture.Text("SELECT state FROM engine_workflow_receipt"));
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_receipt'"));
    }

    private const string TypedReport = """{"state":"succeeded","summary":"done","proofUrls":["https://drive.google.com/x"],"receipt":{"summary":"Inspected","existing":[],"none_found_because":"No applicable code","touches":[]}}""";
    private static void SeedTypedWorker(EngineFixture fixture)
    {
        SeedWorker(fixture);
        fixture.Db.Writer.Write("test.typed-assignment", scope => scope.Execute("""
            INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,base_head,head,owned,state)
            VALUES('ws',1,$machine,1000,1000,'r','step','allocation','/tmp/report-fixture',$head,$head,1,'ready');
            UPDATE engine_step SET result='recon',kind='agent',workspace_id='ws',authority_machine=$machine,authority_session_id='worker',authority_epoch=7 WHERE id='step';
            UPDATE engine_step_attempt SET fingerprint=$fingerprint,workspace_id='ws',session_id='worker',authority_machine=$machine,execution_epoch=7 WHERE step_id='step';
            """, new("$machine", fixture.Machine), new("$head", new string('a', 40)), new("$fingerprint", new string('b', 64))));
    }
}
