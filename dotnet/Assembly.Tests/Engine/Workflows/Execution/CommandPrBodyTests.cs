using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed class CommandPrBodyTests
{
    [Fact]
    public void Actual_preparation_uses_checked_summary_and_only_exact_head_reachable_access()
    {
        using var f = new CommandStepTests.Fixture("printf next");
        var metadata = JsonSerializer.Serialize(new
        {
            receiptId = "checked-build",
            check = new { accepted = true },
            files = new[] { new { path = "capture.png", sha256 = "fixture-hash", bytes = 17 } },
            evidence = new object[] { new { url = "https://example.test/missing-result", testedHead = "tested" },
                new { url = "https://example.test/wrong-head", result = "reachable", testedHead = "other" },
                new { url = "https://example.test/readable", result = "reachable", testedHead = "tested" } }
        });
        f.Core.Db.Writer.Write("test.checked-material", scope => scope.Execute("""
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,epoch,hash,kind,body_json,state,observed_head,checked_ms)
            VALUES('checked-build',1,$machine,1000,1000,$run,$step,0,0,0,'immutable-hash','build',
              '{"report":{"summary":"outer text","receipt":{"summary":"actual checked build summary","commits":[],"tests_added":[]}}}', 'accepted','tested',1000);
            UPDATE engine_step SET receipt_id='checked-build' WHERE id=$step;
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT 'checked-history',1,$machine,1000,1000,$run,COALESCE(MAX(history_seq),0)+1,$step,0,0,'receipt_checked',1000,$metadata
            FROM engine_workflow_history WHERE run_id=$run;
            """, new("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step), new("$metadata", metadata)));
        var prepared = f.Prepare();
        Assert.Contains("actual checked build summary", prepared.PrBody.Text);
        Assert.DoesNotContain("outer text", prepared.PrBody.Text);
        Assert.Contains("Checked file: capture.png", prepared.PrBody.Text);
        Assert.Contains("Access verified: https://example.test/readable", prepared.PrBody.Text);
        Assert.DoesNotContain("missing-result", prepared.PrBody.Text);
        Assert.DoesNotContain("wrong-head", prepared.PrBody.Text);
        Assert.DoesNotContain("PASS", prepared.PrBody.Text);
        using var snapshot = f.Core.Db.OpenSnapshot(default);
        Assert.Equal(WorkflowPrBodies.InputSetHash(snapshot, f.Run, f.Step, 0), prepared.PrBody.InputSetSha256);
    }
}
