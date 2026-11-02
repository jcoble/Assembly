using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Workflows.Execution;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed class CommandRecoveryTests
{
    [Fact]
    public async Task Parent_self_anchor_uses_frozen_public_route_and_raw_or_other_caller_is_refused()
    {
        using var f = new CommandStepTests.Fixture(["printf allowed"], "ask");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        var need = f.Core.Text("SELECT id FROM engine_needs_me")!;
        var answer = JsonSerializer.SerializeToElement("allow_once");
        Assert.Equal("forbidden", f.Core.Apply(new ResolveNeed("answered", answer), need, 1).Error);
        var target = new NeedAnswerTarget(need, 1, "owner", f.Core.Machine, 0, "local", null, "permission", false,
            "owner", f.Core.Machine, "owner", null, null);
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), new(need, ParentNeedAction.Answer, answer), target, null, default);
        f.Core.SeedSession("other");
        Assert.Equal("forbidden", f.Core.Db.Writer.Write("test.wrong-parent", scope =>
            f.Commands.ApplyLocalParentNeed(scope, new McpCaller("other", 0), prepared, 1000)).Error);
        var valid = EngineCommands.PrepareParentNeed(Guid.NewGuid(), new(need, ParentNeedAction.Answer, answer), target, null, default);
        Assert.True(f.Core.Db.Writer.Write("test.parent", scope => f.Commands.ApplyLocalParentNeed(scope, new McpCaller("owner", 0), valid, 1000)).Applied);
        effect = f.Claim(); Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public async Task Nonrepeat_uncertainty_waits_for_controller_and_raw_retry_cannot_launch()
    {
        await using var held = await InterruptedCommand.StartAsync(false);
        await held.InterruptAsync();
        Assert.Equal("waiting", held.Fixture.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, held.Starts);
        var c = held.OwnerContext();
        var raw = EngineCommands.PrepareOwnerNeedCommand(c.CommandId, c.Machine, c.EntityId, 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("retry")), null, null, default);
        Assert.Equal("executor_quiescence_required", held.Fixture.Core.Db.Writer.Write("test.raw-retry", scope =>
            held.Fixture.Commands.ApplyPreparedOwnerNeed(scope, raw, 1000)).Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowCommandRecovery(held.Fixture.Core.Db, held.Fixture.ProofRoot).PrepareAsync(c, default));
        Assert.Equal(1, held.Fixture.Core.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(1, held.Fixture.Core.Count("engine_session"));
    }

    [Fact]
    public async Task Repeat_recovery_requires_actual_prior_exit_and_records_one_next_attempt()
    {
        await using var held = await InterruptedCommand.StartAsync(true);
        await held.InterruptAsync(); var c = held.OwnerContext();
        var recovery = new WorkflowCommandRecovery(held.Fixture.Core.Db, held.Fixture.ProofRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.PrepareAsync(c, default));
        Assert.False(held.Execution.IsCompleted); Assert.Equal(1, held.Starts);
        await held.StopAndLoseExitAsync();
        var prepared = await recovery.PrepareAsync(c, default);
        var result = held.Fixture.Core.Db.Writer.Write("test.repeat-recovery", scope =>
            held.Fixture.Commands.ApplyPreparedWorkflowCommandRetry(scope, prepared.Context, prepared));
        Assert.True(result.Applied, result.Error);
        Assert.Equal(result, held.Fixture.Core.Db.Writer.Write("test.repeat-replay", scope =>
            held.Fixture.Commands.ApplyPreparedWorkflowCommandRetry(scope, prepared.Context with { NowMs = 2000 }, prepared)));
        Assert.True(held.Fixture.Start(held.Fixture.Prepare()).Applied);
        Assert.Equal(2, held.Fixture.Core.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(2, held.Fixture.Core.Count("engine_step_attempt"));
        Assert.Equal(1, held.Fixture.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command' AND state='pending'"));
        Assert.Equal(1, held.Fixture.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_retry_authorized'"));
        Assert.Equal(1, held.Starts);
    }

    private sealed class InterruptedCommand : IAsyncDisposable
    {
        public CommandStepTests.Fixture Fixture { get; }
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Effect Effect { get; }
        public Task<EffectReceipt> Execution { get; }
        public int Starts { get; private set; }
        private InterruptedCommand(bool repeat)
        {
            Fixture = new(["while :; do printf .; done"], "allow", repeat: repeat);
            Assert.True(Fixture.Start(Fixture.Prepare()).Applied); Effect = Fixture.Claim();
            var host = new WorkflowProcess(Fixture.ProofRoot, (effect, intent, process, _) =>
            {
                Starts++;
                var accepted = Fixture.Core.Db.Writer.Write("test.process-observed", scope => Fixture.Commands.ObserveWorkflowProcess(scope,
                    new(Guid.NewGuid(), Fixture.Core.Machine, new(ActorKind.System, "workflow.process"), effect.Id,
                        scope.QueryFirst("SELECT version FROM engine_outbox WHERE id=$id", row => row.Long(0), new SqlArg("$id", effect.Id)), intent.ExecutionEpoch, 1000),
                    new(effect.Id, intent.Generation, intent.Attempt, intent.CommandIndex, intent.ExecutionEpoch, process)));
                _observed.TrySetResult(); return Task.FromResult(accepted.Applied);
            }, (_, _) => Task.FromResult("tree"));
            Execution = host.ExecuteAsync(Effect, false, _stop.Token);
        }
        public static async Task<InterruptedCommand> StartAsync(bool repeat)
        {
            var held = new InterruptedCommand(repeat);
            try { await held._observed.Task.WaitAsync(TimeSpan.FromSeconds(10)); return held; }
            catch { await held.DisposeAsync(); throw; }
        }
        public async Task InterruptAsync()
        {
            Assert.True(Fixture.Core.Apply(new RecoverEffect(), Effect.Id,
                Fixture.Core.Number("SELECT version FROM engine_outbox WHERE id='" + Effect.Id + "'"), actor: new(ActorKind.System, "engine.worker")).Applied);
            Assert.True(Fixture.Core.Apply(new ClaimEffect(), Effect.Id,
                Fixture.Core.Number("SELECT version FROM engine_outbox WHERE id='" + Effect.Id + "'"), actor: new(ActorKind.System, "engine.worker")).Applied);
            var reopened = EffectTransitions.Read(Fixture.Core.Db, Effect.Id, default)!;
            Assert.True(Fixture.Complete(reopened, await Fixture.Host().ReconcileAsync(reopened, default)).Applied);
        }
        public CommandContext OwnerContext() => new(Guid.NewGuid(), Fixture.Core.Machine, new(ActorKind.Window, "owner"),
            Fixture.Core.Text("SELECT id FROM engine_needs_me WHERE state='open'")!, 1, 0, 1000);
        public async Task StopAndLoseExitAsync()
        {
            await _stop.CancelAsync(); var result = await Execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!.Stopped);
            // Simulates a crash before the atomic exit receipt became durable, using only this owned fixture's proof.
            File.Delete(Effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!.OutputPath + ".exit.json");
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await Execution.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _stop.Dispose(); Fixture.Dispose(); }
        }
    }
}
