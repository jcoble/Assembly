using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Fact]
    public void Success_waits_for_external_checks_and_original_journal_replays_after_reopen()
    {
        using var f = new Fixture(); var prepared = f.Prepare();
        var first = f.Apply(prepared); Assert.True(first.Applied, first.Error);
        Assert.Equal("waiting", f.Core.State("engine_step", "step"));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_workflow_receipt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_receipt'"));
        Assert.DoesNotContain("Inspected", f.Core.Text("SELECT payload_json FROM engine_outbox WHERE kind='workflow_receipt'")!);
        f.Core.Reopen(); Assert.Equal(first, f.Apply(prepared));
        Assert.Equal(1, f.Core.Count("engine_workflow_receipt"));
    }

    [Fact]
    public void Same_typed_body_keeps_original_claims_and_does_not_repeat_checks()
    {
        using var f = new Fixture(); var original = f.Prepare(summary: "first"); Assert.True(f.Apply(original).Applied);
        var events = f.Core.Count("engine_event");
        Assert.True(f.Apply(f.Prepare(summary: "changed outer summary")).Applied);
        Assert.Equal(events, f.Core.Count("engine_event"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_receipt'"));
        Assert.Equal(original.BodyJson, f.Core.Text("SELECT body_json FROM engine_workflow_receipt"));
    }

    [Fact]
    public void Different_pending_body_conflicts_and_rejected_correction_needs_new_hash()
    {
        using var f = new Fixture(); var original = f.Prepare(); Assert.True(f.Apply(original).Applied);
        Assert.Equal("report_conflict", f.Apply(f.Prepare(body: Recon.Replace("Inspected", "Different"))).Error);
        f.Core.Db.Writer.Write("test.reject", s => s.Execute("UPDATE engine_workflow_receipt SET state='rejected',error='existing'"));
        Assert.True(f.Apply(f.Prepare()).Applied);
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_receipt'"));
        Assert.True(f.Apply(f.Prepare(body: Recon.Replace("Inspected", "Corrected"))).Applied);
        Assert.Equal(2, f.Core.Count("engine_workflow_receipt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_receipt WHERE state='rejected'"));
    }

    [Fact]
    public void Missing_receipt_shape_and_stale_capture_cannot_create_a_candidate()
    {
        using var f = new Fixture();
        Assert.Equal("receipt_required", f.Apply(f.Prepare(receipt: false)).Error);
        Assert.Equal("receipt.none_found_because", f.Apply(f.Prepare(body: """{"summary":"x","existing":[],"touches":[]}""")).Error);
        var prepared = f.Prepare();
        f.Core.Db.Writer.Write("test.generation", s => s.Execute("UPDATE engine_step SET generation=1 WHERE id='step'"));
        Assert.Equal("wrong_attempt", f.Apply(prepared).Error);
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
    }

    [Fact]
    public void Large_receipt_accepts_once_and_same_command_changed_last_byte_conflicts()
    {
        using var f = new Fixture();
        var body = Recon.Replace("Inspected", new string('x', 40000) + "🌍");
        var original = f.Prepare(body: body); Assert.True(original.BodyBytes > 32768);
        Assert.True(f.Apply(original).Applied); f.Core.Reopen(); Assert.True(f.Apply(original).Applied);
        var changed = f.Prepare(body: body.Replace("🌍", "🌎"), command: original.Receipt!.Context.CommandId, version: original.Receipt.Context.ExpectedVersion);
        Assert.Equal("command_id_conflict", f.Apply(changed).Error);
        Assert.Equal(original.BodyJson, f.Core.Text("SELECT body_json FROM engine_workflow_receipt"));
    }

    [Fact]
    public void Sql_failure_rolls_back_receipt_attempt_effect_history_and_root_journal()
    {
        using var f = new Fixture(); var prepared = f.Prepare();
        var journals = f.Core.Count("engine_command_receipt"); var events = f.Core.Count("engine_event");
        f.Core.Db.Writer.Write("test.fault", s => s.Execute("""
            CREATE TRIGGER reject_receipt_history BEFORE INSERT ON engine_workflow_history
            BEGIN SELECT RAISE(ABORT,'receipt history fault'); END;
            """));
        Assert.Throws<SqliteException>(() => f.Apply(prepared));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt")); Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal("running", f.Core.State("engine_step", "step"));
        Assert.Null(f.Core.Text("SELECT receipt_id FROM engine_step_attempt"));
    }

    [Fact]
    public void Original_receipt_replay_never_reconstructs_a_missing_root_journal()
    {
        using var f = new Fixture(); var prepared = f.Prepare(); Assert.True(f.Apply(prepared).Applied);
        var body = prepared.Receipt!;
        var replay = WorkflowPreparation.Receipt(body.Context, body.Report, body.Capture, default, replay: true);
        Assert.True(f.Apply(replay).Applied);
        f.Core.Db.Writer.Write("test.corrupt", s => s.Execute("DELETE FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", body.Context.CommandId.ToString("N"))));
        var count = f.Core.Count("engine_command_receipt");
        Assert.Equal("receipt_unknown", f.Apply(replay).Error);
        Assert.Equal(count, f.Core.Count("engine_command_receipt"));
    }

    private const string Recon = """{"summary":"Inspected","existing":[],"none_found_because":"No applicable code","touches":[]}""";
    private sealed class Fixture : IDisposable
    {
        internal EngineFixture Core { get; } = new();
        internal Fixture()
        {
            Core.SeedSession("owner"); Core.SeedRun(ownerSessionId: "owner", state: "running");
            Core.SeedSession("worker", parentId: "owner", runId: "r", epoch: 7); Core.SeedStep();
            Assert.True(Core.Apply(new StartStep("worker"), "step", epoch: 7).Applied);
            Core.Db.Writer.Write("test.workflow-assignment", s => s.Execute("""
                INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,base_head,head,owned,state)
                VALUES('ws',1,$machine,1000,1000,'r','step','allocation','/tmp/receipt-fixture',$head,$head,1,'ready');
                UPDATE engine_step SET result='recon',kind='agent',workspace_id='ws',authority_machine=$machine,authority_session_id='worker',authority_epoch=7;
                UPDATE engine_step_attempt SET fingerprint=$fingerprint,workspace_id='ws',session_id='worker',authority_machine=$machine,execution_epoch=7;
                """, new("$machine", Core.Machine), new("$head", new string('a', 40)), new("$fingerprint", new string('b', 64))));
        }
        internal PreparedWorkflowCommand Prepare(string? body = null, string summary = "done", Guid? command = null, long? version = null, bool receipt = true)
        {
            var context = new CommandContext(command ?? Guid.NewGuid(), Core.Machine, new(ActorKind.Session, "worker"), "step",
                version ?? Core.Number("SELECT version FROM engine_step WHERE id='step'"), 7, 1000);
            using var json = JsonDocument.Parse(body ?? Recon);
            return WorkflowPreparation.Receipt(context, new(1, 0, "succeeded", summary, ["https://evidence.invalid/proof"], receipt ? json.RootElement : null),
                new("step", 1, 0, "worker", 7, "ws", 1, new string('a', 40), new string('b', 64), "execution-1"), default);
        }
        internal CommandResult Apply(PreparedWorkflowCommand prepared) => Core.Db.Writer.Write("test.workflow-receipt", s =>
            new EngineCommands(Core.Machine, Core.Limits).ApplyPreparedWorkflow(s, prepared.Receipt!.Context with { NowMs = 1000 }, prepared));
        public void Dispose() => Core.Dispose();
    }
}
