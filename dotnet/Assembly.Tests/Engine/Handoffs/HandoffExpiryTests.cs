using Assembly.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Execution;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine;

public sealed class HandoffExpiryTests
{
    [Fact]
    public async Task Requested_handoff_expires_on_the_existing_worker_deadline_and_restores_physical_local_owner()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        var passes = new ExpiryPasses();
        using var stopped = new CancellationTokenSource();
        var worker = new EngineWorker(f.Db, new EngineCommands(f.Machine, f.Limits), f.Machine, f.Time, null, passes);
        var task = worker.RunAsync(stopped.Token);
        try
        {
            await passes.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("requested", f.State("engine_handoff", "H"));
            f.Time.Advance(TimeSpan.FromMinutes(5));
            await passes.Expired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("failed", f.State("engine_handoff", "H"));
            Assert.Equal("expired", f.Text("SELECT reason FROM engine_handoff WHERE id='H'"));
            Assert.Equal("idle", f.State("engine_session", "s"));
            Assert.Equal("local", f.Text("SELECT holder FROM engine_session WHERE id='s'"));
            Assert.Equal(7, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
            Assert.Null(f.Text("SELECT reserved_epoch FROM engine_session WHERE id='s'"));
            Assert.Equal("pending", f.Text("SELECT state FROM engine_outbox WHERE kind='release'"));
        }
        finally
        {
            await stopped.CancelAsync();
            await task;
        }
    }

    private sealed class ExpiryPasses : IEngineRetention
    {
        internal TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Expired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? NextDeadlineMs(IReadScope scope, long nowMs) => null;
        public void Apply(IWriteScope scope, long nowMs)
        {
            scope.AfterCommit(() => First.TrySetResult());
            if (scope.QueryFirst("SELECT state FROM engine_handoff WHERE id='H'", row => row.Text(0)) == "failed")
                scope.AfterCommit(() => Expired.TrySetResult());
        }
    }
}
