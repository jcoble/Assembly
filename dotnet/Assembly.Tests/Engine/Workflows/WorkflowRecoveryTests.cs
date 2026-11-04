using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retry_terminal_run_creates_new_id_and_original_is_immutable(bool failedOnly)
    {
        using var f = new Fixture();
        f.Terminal(); var before = f.Original();
        var (c, p) = f.Rerun(failedOnly);
        var ack = f.Apply(c, p);
        Assert.True(ack.Result.Applied, ack.Result.Error); Assert.NotEqual(f.Run, ack.RunId);
        Assert.Equal(f.Run, f.Core.Text("SELECT rerun_of FROM engine_run WHERE id=$id", new SqlArg("$id", ack.RunId)));
        Assert.Equal(before, f.Original()); Assert.Equal(2, f.Core.Count("engine_run"));
        f.Core.Reopen();
        var (again, replay) = f.Rerun(failedOnly);
        var repeated = f.Apply(again, replay);
        Assert.Equal(ack.RunId, repeated.RunId); Assert.Equal(ack.Result, repeated.Result);
        Assert.Equal(2, f.Core.Count("engine_run"));
    }
    [Fact]
    public void New_run_start_ack_returns_original_request_key_uuid()
    {
        using var f = new Fixture(); f.Terminal(); var (c, p) = f.Rerun(false);
        var original = f.Apply(c, p); var (different, replay) = f.Rerun(false);
        Assert.NotEqual(c.EntityId, different.EntityId);
        Assert.Equal(original, f.Apply(different, replay));
    }
    [Fact]
    public void Rerun_refuses_nonterminal_sources_stale_preparation_and_changed_request_key_body()
    {
        using var f = new Fixture(); var (c, p) = f.Rerun(false);
        Assert.Equal("invalid_transition", f.Apply(c, p).Result.Error);
        f.Terminal(); (c, p) = f.Rerun(false);
        f.Core.Db.Writer.Write("test.source-moved", s => s.Execute("UPDATE engine_run SET version=version+1 WHERE id=$id", new SqlArg("$id", f.Run)));
        Assert.Equal("version_conflict", f.Apply(c, p).Result.Error);
        (c, p) = f.Rerun(false); Assert.True(f.Apply(c, p).Result.Applied);
        (c, p) = f.Rerun(true); Assert.Equal("request_key_conflict", f.Apply(c, p).Result.Error);
    }
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Retry_seeds_only_checked_same_head_ancestors_and_keeps_provenance(bool sameHead, bool checkedEvidence, bool inherit)
    {
        using var f = new Fixture(); f.Terminal();
        var head = new string('a', 40); var sourceStep = f.Core.Text("SELECT id FROM engine_step WHERE run_id=$id AND definition_key='build'", new SqlArg("$id", f.Run))!;
        f.Core.Db.Writer.Write("test.checked-ancestor", s => s.Execute("""
            UPDATE engine_run SET work_head=$head WHERE id=$run;
            UPDATE engine_step SET state='succeeded',receipt_id='receipt',attempt=1 WHERE id=$step;
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,epoch,hash,kind,body_json,state,observed_head,checked_ms)
            VALUES('receipt',1,$machine,1000,1000,$run,$step,1,0,0,'hash','build','{"report":{"summary":"Original checked output"}}','accepted',$head,1000);
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            VALUES('check',1,$machine,1000,1000,$run,2,$step,1,0,'receipt_checked',1000,
              json_object('receiptId','receipt','check',json_object('accepted',json($accepted),'head',$head)));
            """, new("$head", head), new("$run", f.Run), new("$step", sourceStep), new("$machine", f.Core.Machine), new("$accepted", checkedEvidence ? "true" : "false")));
        var (c, p) = f.Rerun(true); var ack = f.Apply(c, p); Assert.True(ack.Result.Applied, ack.Result.Error);
        f.Core.Db.Writer.Write("test.confirm-workspace-head", s =>
        {
            s.Execute("UPDATE engine_run SET setup_state='ready',work_head=$head,pr_body='/proof/pr-body.md' WHERE id=$id", new("$head", sameHead ? head : new string('b', 40)), new("$id", ack.RunId));
            return WorkflowRetrySeeds.Apply(s, c, ack.RunId);
        });
        Assert.Equal(inherit ? "succeeded" : "pending", f.Core.Text("SELECT state FROM engine_step WHERE run_id=$run AND definition_key='build'", new SqlArg("$run", ack.RunId)));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_step WHERE run_id=$run AND definition_key='check'", new SqlArg("$run", ack.RunId)));
        Assert.Equal(inherit ? 1 : 0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE run_id=$run AND kind='retry_inherited'", new SqlArg("$run", ack.RunId)));
        using var read = f.Core.Db.OpenSnapshot(default);
        var document = Assembly.Engine.Workflows.Execution.WorkflowPrBodies.Prepare(read, ack.RunId, "check", 0);
        Assert.Equal(inherit, document.Text.Contains("Original checked output", StringComparison.Ordinal));
    }
    private sealed class Fixture : IDisposable
    {
        public EngineFixture Core { get; } = new();
        public string Run { get; }
        public Fixture()
        {
            Core.SeedSession("owner"); var store = new ProjectStore(Core.Db, Core.Time);
            var project = Core.Db.Writer.Write("test.project", s => store.AddOrRestore(s, Core.Machine, "/test", "Test", null).Project);
            var request = new WorkflowStartRequest(Guid.NewGuid(), Guid.NewGuid().ToString("N"), 0, 0, "owner", "original", ".assembly/workflows/test.yml", project.Id, "Request", null, JsonDocument.Parse("{}").RootElement.Clone());
            var c = new CommandContext(request.CommandId, Core.Machine, new(ActorKind.Window, "owner"), request.ProposedRunId, 0, 0, 1000);
            var snapshot = WorkflowParser.Parse(new("name: Retry\nsteps:\n  build: {run: ['true']}\n  check: {needs: [build], run: ['true']}\n", ImmutableSortedDictionary<string, string>.Empty));
            Run = Apply(c, WorkflowStarts.Prepare(c, request, snapshot, project)).RunId;
        }
        public void Terminal() => Core.Db.Writer.Write("test.terminal", s => s.Execute("UPDATE engine_run SET state='failed' WHERE id=$id; UPDATE engine_step SET state='failed' WHERE run_id=$id; UPDATE engine_outbox SET state='done' WHERE run_id=$id AND kind='workflow_setup'", new SqlArg("$id", Run)));
        public string Original() => Core.Text("SELECT json_object('version',version,'state',state,'updated',updated_ms,'rerun',rerun_of) FROM engine_run WHERE id=$id", new SqlArg("$id", Run))!;
        public (CommandContext, PreparedWorkflowCommand) Rerun(bool only)
        {
            var c = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.Window, "owner"), Guid.NewGuid().ToString("N"), 0, 0, 1000);
            using var s = Core.Db.OpenSnapshot(default);
            return (c, WorkflowStarts.PrepareRerun(s, c, only ? new RetryFailedWorkflow(Run, "again") : new RerunWorkflow(Run, "again")));
        }
        public WorkflowStartAck Apply(CommandContext c, PreparedWorkflowCommand p) => Core.Db.Writer.Write("test.start", s => WorkflowStarts.Apply(s, new(Core.Machine, Core.Limits), c, p));
        public void Dispose() => Core.Dispose();
    }
}
