using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Execution;
using Assembly.Engine.Workflows.Runs;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed partial class WorkflowWorkerTests
{
    [Fact]
    public async Task More_than_one_hundred_ready_gates_drain_without_another_wake()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "running");
        for (var i = 0; i < 205; i++) f.SeedStep("gate" + i);
        f.Db.Writer.Write("test.gates", s => s.Execute("""
            UPDATE engine_run SET definition_hash='workflow';
            UPDATE engine_step SET kind='gate',definition_key=id;
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('r',1,$machine,1000,1000,'r',1,'workflow','inputs','{"snapshot":{"definition":{"steps":{"gate":{"gate":"Approve"}}}}}');
            """, new SqlArg("$machine", f.Machine)));
        using var stop = new CancellationTokenSource(); using var passes = new Passes(topics);
        var worker = new EngineWorker(f.Db, new(f.Machine, f.Limits), f.Machine, f.Time, null, passes);
        var running = worker.RunAsync(stop.Token);
        try
        {
            while (f.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'") < 205) await passes.Next();
            Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_step WHERE state='waiting'"));
            Assert.Equal(205, f.Count("engine_step_attempt"));
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Undo_expiry_uses_the_earliest_deadline_wait_and_clears_token_without_changing_outcome()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        f.SeedSession("owner"); f.SeedRun(ownerSessionId: "owner", state: "failed"); f.SeedStep(state: "failed");
        f.Db.Writer.Write("test.workflow", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'"));
        Assert.True(f.Apply(new KeepWorkflowSucceeded(), "r").Applied);
        f.Reopen();
        var clock = new TimerObserver(f.Time); using var stop = new CancellationTokenSource(); using var passes = new Passes(topics);
        var worker = new EngineWorker(f.Db, new(f.Machine, f.Limits), f.Machine, clock, null, passes);
        var running = worker.RunAsync(stop.Token);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(5), await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            f.Time.Advance(TimeSpan.FromMilliseconds(4999)); Assert.NotNull(f.Text("SELECT keep_token FROM engine_run"));
            f.Time.Advance(TimeSpan.FromMilliseconds(1));
            while (f.Text("SELECT keep_token FROM engine_run") is not null) await passes.Next();
            Assert.Null(f.Text("SELECT keep_until_ms FROM engine_run"));
            Assert.Equal("failed", f.State("engine_run", "r")); Assert.Equal(1, f.Number("SELECT closed FROM engine_step"));
            Assert.Equal("workflow.keep_expired", f.Events().Last().Kind);
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class TimerObserver(TimeProvider inner) : TimeProvider
    {
        public Channel<TimeSpan> Created { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period); Created.Writer.TryWrite(dueTime); return timer;
        }
    }
}
