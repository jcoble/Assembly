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
