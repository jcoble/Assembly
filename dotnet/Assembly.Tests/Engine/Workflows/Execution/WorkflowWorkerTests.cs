using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Execution;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Scheduling;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed class WorkflowWorkerTests
{
    [Fact]
    public async Task Preparation_uses_same_writer_outside_scope_and_runs_once_per_pushed_wake()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); using var stop = new CancellationTokenSource();
        using var passes = new Passes(topics); var calls = 0;
        var worker = new EngineWorker(f.Db, new(f.Machine, f.Limits), f.Machine, f.Time, null, passes, _ =>
        {
            Interlocked.Increment(ref calls);
            f.Db.Writer.Write("test.prepare-outside-writer", scope => scope.Execute("UPDATE engine_meta SET next_seq=next_seq"));
            return Task.FromResult<IReadOnlyList<PreparedWorkflowDispatch>>([]);
        });
        var running = worker.RunAsync(stop.Token);
        try
        {
            await passes.Next(); Assert.Equal(1, calls);
            f.Time.Advance(TimeSpan.FromDays(1));
            Assert.Equal(1, calls); // No deadline or interval was installed by the no-op preparation.
            worker.Wake(); await passes.Next(); Assert.Equal(2, calls);
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Worker_shutdown_cancels_owned_preparation_without_another_scheduler()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); using var stop = new CancellationTokenSource();
        using var passes = new Passes(topics);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<IReadOnlyList<PreparedWorkflowDispatch>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new EngineWorker(f.Db, new(f.Machine, f.Limits), f.Machine, f.Time, null, passes, async ct =>
        { entered.SetResult(); return await hold.Task.WaitAsync(ct); });
        var running = worker.RunAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(hold.Task.IsCompleted);
    }

    [Fact]
    public async Task Persisted_command_deadline_wakes_the_existing_worker_without_a_pushed_wake()
    {
        var topics = new Topics(); using var f = new CommandStepTests.Fixture(["printf must-not-launch"], "allow", topics: topics);
        Assert.True(f.Start(f.Prepare()).Applied);
        var deadline = f.Core.Number("SELECT deadline_ms FROM engine_step");
        using var stop = new CancellationTokenSource(); using var passes = new Passes(topics);
        var worker = new EngineWorker(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time, null, passes);
        var running = worker.RunAsync(stop.Token);
        try
        {
            await passes.Next();
            Assert.Equal("running", f.Core.Text("SELECT state FROM engine_step"));
            f.Core.Time.Advance(TimeSpan.FromMilliseconds(deadline - f.Core.Time.GetUtcNow().ToUnixTimeMilliseconds()));
            await passes.Next();
            Assert.Equal("timeout_pending", f.Core.Text("SELECT dispatch_state FROM engine_step"));
            Assert.Equal("timed_out", f.Core.Text("SELECT outcome_reason FROM engine_step"));
            Assert.Equal(1, f.Core.Number("SELECT cancel_requested FROM engine_step_attempt"));
            Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE executor_json IS NOT NULL"));
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Passes : IEngineRetention, IDisposable
    {
        private readonly Channel<bool> _passes = Channel.CreateUnbounded<bool>();
        private readonly IDisposable _subscription;
        public Passes(Topics topics) => _subscription = topics.Subscribe("test.workflow.pass", null, _ => { }, (_, _) => _passes.Writer.TryWrite(true));
        public void Apply(IWriteScope scope, long now) => scope.Publish("test.workflow.pass", null, now, EngineJson.Default.Int64);
        public void Dispose() => _subscription.Dispose();
        public long? NextDeadlineMs(IReadScope scope, long now) => null;
        public async Task Next() => await _passes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
