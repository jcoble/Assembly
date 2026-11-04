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
        var context = c with { EntityId = held.Fixture.Run, ExpectedVersion = held.Fixture.Core.Number("SELECT version FROM engine_run") };
        var command = new Assembly.Engine.Workflows.Runs.RetryWorkflowStep(held.Fixture.Step);
        var result = held.Fixture.Core.Db.Writer.Write("test.repeat-recovery", scope =>
            held.Fixture.Commands.ApplyPreparedWorkflowControlRetry(scope, context, command, prepared));
        Assert.True(result.Applied, result.Error);
        Assert.Equal(result, held.Fixture.Core.Db.Writer.Write("test.repeat-replay", scope =>
            held.Fixture.Commands.ApplyPreparedWorkflowControlRetry(scope, context with { NowMs = 2000 }, command, prepared)));
        Assert.True(held.Fixture.Start(held.Fixture.Prepare()).Applied);
        Assert.Equal(2, held.Fixture.Core.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(2, held.Fixture.Core.Count("engine_step_attempt"));
        Assert.Equal(1, held.Fixture.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command' AND state='pending'"));
        Assert.Equal(1, held.Fixture.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_retry_authorized'"));
        Assert.Equal(1, held.Starts);
    }

    [Fact]
    public async Task Standard_run_retry_uses_checked_absence_and_keeps_the_window_command_receipt()
    {
        await using var held = await InterruptedCommand.StartAsync(true);
        await held.InterruptAsync(); var f = held.Fixture;
        var need = held.OwnerContext();
        var context = need with { EntityId = f.Run, ExpectedVersion = f.Core.Number("SELECT version FROM engine_run") };
        var command = new Assembly.Engine.Workflows.Runs.RetryWorkflowStep(f.Core.Text("SELECT id FROM engine_step")!);
        Assert.Equal("executor_quiescence_required", f.Core.Apply(context, command).Error);
        await held.StopAndLoseExitAsync();
        var prepared = await new WorkflowCommandRecovery(f.Core.Db, f.ProofRoot).PrepareAsync(need, default);
        context = context with { CommandId = Guid.NewGuid() };
        var result = f.Core.Db.Writer.Write("test.standard-retry", scope => f.Commands.ApplyPreparedWorkflowControlRetry(scope, context, command, prepared));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT generation FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(result, f.Core.Apply(context, command));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_retry_authorized'"));
    }

    [Fact]
    public async Task Cancelled_parked_unknown_waits_for_exact_saved_executor_to_stop()
    {
        await using var held = await InterruptedCommand.StartAsync(false);
        await held.InterruptAsync(); var f = held.Fixture;
        var c = held.OwnerContext();
        var answer = EngineCommands.PrepareOwnerNeedCommand(c.CommandId, c.Machine, c.EntityId, 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("cancel")), null, null, default);
        Assert.True(f.Core.Db.Writer.Write("test.cancel-parked", scope => f.Commands.ApplyPreparedOwnerNeed(scope, answer, 1000)).Applied);
        Assert.False(held.Execution.IsCompleted);
        using (var snapshot = f.Core.Db.OpenSnapshot(default)) Assert.False(CommandStepTransitions.ExecutionsDrained(snapshot, f.Run));
        var intent = held.Effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        var stop = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"), held.Effect.Id,
            f.Core.Number("SELECT version FROM engine_outbox WHERE id='" + held.Effect.Id + "'"), intent.ExecutionEpoch, 1000);
        Assert.True(f.Core.Db.Writer.Write("test.stop-reconcile", scope => f.Commands.ReconcileWorkflowCommandStop(scope, stop)).Applied);
        var effect = f.Claim(); var receipt = await f.Host().ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.True(f.Complete(effect, receipt).Applied);
        await held.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_step"));
        using (var snapshot = f.Core.Db.OpenSnapshot(default)) Assert.True(CommandStepTransitions.ExecutionsDrained(snapshot, f.Run));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_stop_requested'"));
        Assert.Equal(1, held.Starts);
    }

    [Fact]
    public async Task Parked_policy_cancellation_reconciles_as_known_not_launched()
    {
        using var f = new CommandStepTests.Fixture(["printf must-not-launch"], "ask");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        f.Core.Db.Writer.Write("test.run-cancel", scope => scope.Execute("UPDATE engine_run SET state='cancelling'; UPDATE engine_step_attempt SET cancel_requested=1"));
        var intent = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        var c = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"), effect.Id,
            f.Core.Number("SELECT version FROM engine_outbox WHERE id='" + effect.Id + "'"), intent.ExecutionEpoch, 1000);
        Assert.True(f.Core.Db.Writer.Write("test.stop-policy", scope => f.Commands.ReconcileWorkflowCommandStop(scope, c)).Applied);
        effect = f.Claim(); var receipt = await f.Host().ReconcileAsync(effect, default);
        Assert.True(receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!.NotStarted);
        Assert.True(f.Complete(effect, receipt).Applied);
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE executor_json IS NOT NULL"));
    }

    [Fact]
    public async Task Lost_exit_stop_fact_preserves_unknown_exit_and_output_after_positive_absence()
    {
        await using var held = await InterruptedCommand.StartAsync(false);
        await held.InterruptAsync(); await held.StopAndLoseExitAsync();
        var f = held.Fixture; var c = held.OwnerContext();
        var answer = EngineCommands.PrepareOwnerNeedCommand(c.CommandId, c.Machine, c.EntityId, 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("cancel")), null, null, default);
        Assert.True(f.Core.Db.Writer.Write("test.cancel-absent", scope => f.Commands.ApplyPreparedOwnerNeed(scope, answer, 1000)).Applied);
        var intent = held.Effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        var stop = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"), held.Effect.Id,
            f.Core.Number("SELECT version FROM engine_outbox WHERE id='" + held.Effect.Id + "'"), intent.ExecutionEpoch, 1000);
        Assert.True(f.Core.Db.Writer.Write("test.absent-reconcile", scope => f.Commands.ReconcileWorkflowCommandStop(scope, stop)).Applied);
        var effect = f.Claim(); var receipt = await f.Host().ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        var stopped = receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        Assert.True(stopped.ExecutorAbsent); Assert.True(stopped.Stopped);
        Assert.Null(stopped.ExitCode); Assert.Equal("", stopped.OutputReceiptPath); Assert.Equal("", stopped.OutputSha256);
        Assert.True(f.Complete(effect, receipt).Applied);
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, held.Starts);
    }

    [Fact]
    public async Task Changed_prepared_body_records_known_no_launch_and_cancel_only_need_that_drains()
    {
        using var f = new CommandStepTests.Fixture(["printf must-not-run"], "allow", repeat: true);
        var preparedStart = f.Prepare(); Assert.True(f.Start(preparedStart).Applied);
        await File.WriteAllTextAsync(preparedStart.PrBody.Path, "changed after preparation");
        var effect = f.Claim(); var stale = await f.Host().ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Unavailable, stale.Disposition); Assert.Equal("preparation_stale", stale.Error);
        Assert.True(stale.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!.NotStarted);
        Assert.True(f.Complete(effect, stale).Applied);
        Assert.Equal("preparation_stale", f.Core.Text("SELECT kind FROM engine_needs_me WHERE state='open'"));
        Assert.Equal("[\"cancel\"]", f.Core.Text("SELECT options_json FROM engine_needs_me WHERE state='open'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_not_started' AND json_extract(safe_body_json,'$.notStarted')=1"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE executor_json IS NOT NULL"));
        var need = f.Core.Text("SELECT id FROM engine_needs_me WHERE state='open'")!;
        var retry = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, need, 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("retry")), null, null, default);
        Assert.Equal("executor_quiescence_required", f.Core.Db.Writer.Write("test.refused-stale-retry", scope => f.Commands.ApplyPreparedOwnerNeed(scope, retry, 1000)).Error);
        var cancel = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, need, 1, 0,
            new ResolveNeed("answered", JsonSerializer.SerializeToElement("cancel")), null, null, default);
        Assert.True(f.Core.Db.Writer.Write("test.cancel-stale", scope => f.Commands.ApplyPreparedOwnerNeed(scope, cancel, 1000)).Applied);
        var intent = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        var c = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"), effect.Id,
            f.Core.Number("SELECT version FROM engine_outbox WHERE id='" + effect.Id + "'"), intent.ExecutionEpoch, 1000);
        Assert.True(f.Core.Db.Writer.Write("test.stale-stop", scope => f.Commands.ReconcileWorkflowCommandStop(scope, c)).Applied);
        effect = f.Claim(); var stopped = await f.Host().ReconcileAsync(effect, default);
        Assert.True(stopped.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!.NotStarted);
        Assert.True(f.Complete(effect, stopped).Applied);
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT attempt FROM engine_step"));
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
