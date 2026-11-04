using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Execution;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Workspaces;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed class CommandStepTests
{
    [Fact]
    public async Task Ordinary_nonzero_collects_failure_and_runs_remaining_commands_without_provider_session()
    {
        using var f = new Fixture("printf first; exit 7", "printf second");
        var first = f.Prepare(); Assert.True(f.Start(first).Applied);
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal(1, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        var effect = f.Claim(); var result = await f.Host().ExecuteAsync(effect, default);
        Assert.True(f.Complete(effect, result).Applied);
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT command_index FROM engine_step_attempt"));
        Assert.True(f.Start(f.Prepare()).Applied);
        effect = f.Claim(); result = await f.Host().ExecuteAsync(effect, default);
        Assert.True(f.Complete(effect, result).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_ended'"));
        Assert.Equal(1, f.Core.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(1, f.Core.Count("engine_session"));
    }

    [Fact]
    public async Task Policy_ask_commits_real_workflow_need_and_one_checked_continuation()
    {
        using var f = new Fixture(["printf allowed"], "ask");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        var blocked = await f.Host().ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Unavailable, blocked.Disposition); Assert.True(f.Complete(effect, blocked).Applied);
        Assert.Equal("unavailable", f.Core.Text("SELECT state FROM engine_outbox WHERE kind='workflow_command'"));
        Assert.Equal("workflow", f.Core.Text("SELECT source_kind FROM engine_needs_me"));
        Assert.Equal("owner", f.Core.Text("SELECT session_id FROM engine_needs_me"));
        Assert.False(File.Exists(effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!.OutputPath));
        var need = f.Core.Text("SELECT id FROM engine_needs_me")!;
        var context = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), need, 1, 0, 1000);
        var answer = new ResolveNeed("answered", JsonSerializer.SerializeToElement("allow_once"));
        var preparedAnswer = EngineCommands.PrepareOwnerNeedCommand(context.CommandId, context.Machine, need, 1, 0, answer, null, null, default);
        var accepted = f.Core.Db.Writer.Write("test.workflow.answer", s => f.Commands.ApplyPreparedOwnerNeed(s, preparedAnswer, 1000));
        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_outbox WHERE kind='workflow_command'"));
        effect = f.Claim(); Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_ended'"));
        var duplicate = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), context.Machine, need, 1, 0, answer, null, null, default);
        Assert.Equal("already_resolved", f.Core.Db.Writer.Write("test.workflow.duplicate", s => f.Commands.ApplyPreparedOwnerNeed(s, duplicate, 1000)).Error);
    }

    [Fact]
    public async Task Denied_command_settles_without_a_process_or_provider_answer()
    {
        using var f = new Fixture(["printf denied"], "ask");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        Assert.True(f.Complete(effect, await f.Host().ExecuteAsync(effect, default)).Applied);
        var need = f.Core.Text("SELECT id FROM engine_needs_me")!;
        var c = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), need, 1, 0, 1000);
        var answer = new ResolveNeed("answered", JsonSerializer.SerializeToElement("deny"));
        var prepared = EngineCommands.PrepareOwnerNeedCommand(c.CommandId, c.Machine, need, 1, 0, answer, null, null, default);
        Assert.True(f.Core.Db.Writer.Write("test.deny", s => f.Commands.ApplyPreparedOwnerNeed(s, prepared, 1000)).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE executor_json IS NOT NULL"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public async Task Committed_cancel_before_host_launch_records_no_process_and_drains_run()
    {
        using var f = new Fixture("printf must-not-run");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        f.Core.Db.Writer.Write("test.cancel", s => s.Execute("""
            UPDATE engine_step_attempt SET cancel_requested=1;
            UPDATE engine_run SET state='cancelling';
            """));
        var receipt = await f.Host().ExecuteAsync(effect, default);
        var result = receipt.Data.Deserialize(WorkflowExecutionJson.Default.CommandExecutionReceipt)!;
        Assert.True(result.NotStarted); Assert.Null(result.ExitCode); Assert.Null(result.Process);
        Assert.True(f.Complete(effect, receipt).Applied);
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal("cancelled", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE executor_json IS NOT NULL"));
    }

    [Fact]
    public async Task Changed_code_fingerprint_fails_and_old_generation_confirmation_is_history_only()
    {
        using var f = new Fixture("printf changed");
        Assert.True(f.Start(f.Prepare()).Applied); var effect = f.Claim();
        var result = await f.Host("other-tree").ExecuteAsync(effect, default);
        Assert.True(f.Complete(effect, result).Applied);
        Assert.Equal("changed_code", f.Core.Text("SELECT outcome_reason FROM engine_step"));
        using var stale = new Fixture("printf old");
        Assert.True(stale.Start(stale.Prepare()).Applied); var old = stale.Claim();
        var oldResult = await stale.Host().ExecuteAsync(old, default);
        stale.Core.Db.Writer.Write("test.next-generation", s => s.Execute("UPDATE engine_step SET generation=1"));
        Assert.True(stale.Complete(old, oldResult).Applied);
        Assert.Equal("running", stale.Core.Text("SELECT state FROM engine_step_attempt"));
        Assert.Equal(1, stale.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='command_late'"));
    }

    [Fact]
    public async Task Earlier_observed_code_change_survives_a_later_restored_tree()
    {
        using var f = new Fixture("printf changed", "printf restored");
        Assert.True(f.Start(f.Prepare()).Applied); var first = f.Claim();
        Assert.True(f.Complete(first, await f.Host("changed-tree").ExecuteAsync(first, default)).Applied);
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_step"));
        Assert.True(f.Start(f.Prepare()).Applied); var last = f.Claim();
        Assert.True(f.Complete(last, await f.Host("tree").ExecuteAsync(last, default)).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal("changed_code", f.Core.Text("SELECT outcome_reason FROM engine_step"));
    }

    [Fact]
    public async Task Command_on_fail_uses_real_receipts_after_list_settlement_and_fences_one_redo()
    {
        using var f = new Fixture(["exit 7", "printf collected"], "allow", true);
        await f.ReadyBuildAsync();
        Assert.True(f.Start(f.Prepare()).Applied);
        var first = f.Claim(); Assert.True(f.Complete(first, await f.Host().ExecuteAsync(first, default)).Applied);
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_requested'"));
        Assert.True(f.Start(f.Prepare()).Applied);
        var last = f.Claim(); Assert.True(f.Complete(last, await f.Host().ExecuteAsync(last, default)).Applied);
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal("waiting", f.Core.Text("SELECT state FROM engine_step WHERE definition_key='checks'"));
        Assert.Equal("redo_pending", f.Core.Text("SELECT dispatch_state FROM engine_step WHERE definition_key='build'"));
        Assert.Equal("command", f.Core.Text("SELECT json_extract(safe_body_json,'$.sourceKind') FROM engine_workflow_history WHERE kind='redo_requested'"));
        Assert.Equal(0, f.Core.Count("engine_workflow_receipt"));
        var build = f.Core.Text("SELECT id FROM engine_step WHERE definition_key='build'")!;
        var c = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "workflow.redo"), build,
            f.Core.Number("SELECT version FROM engine_step WHERE definition_key='build'"), 0, 1000);
        var refused = f.Core.Db.Writer.Write("test.redo-not-released", scope =>
            Assembly.Engine.Workflows.Reviews.ReviewTransitions.Advance(scope, c, (_, _) => false));
        Assert.Equal("executor_quiescence_required", refused.Error);
        var applied = f.Core.Db.Writer.Write("test.redo", scope => EngineJournal.Execute(scope, c, "\"command-redo\"", () =>
            Assembly.Engine.Workflows.Reviews.ReviewTransitions.Advance(scope, c, (read, fence) => read.QueryFirst("""
                SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE kind='workflow_command' AND state='done'
                  AND json_extract(payload_json,'$.stepId')=$step AND json_extract(payload_json,'$.generation')=$generation
                  AND json_extract(payload_json,'$.attempt')=$attempt)
                AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE kind='workflow_command' AND state NOT IN ('done','failed','dead')
                  AND json_extract(payload_json,'$.stepId')=$step AND json_extract(payload_json,'$.generation')=$generation
                  AND json_extract(payload_json,'$.attempt')=$attempt)
                """, row => row.Long(0) != 0, new SqlArg("$step", fence.StepId), new("$generation", fence.Generation), new("$attempt", fence.Attempt)))));
        Assert.True(applied.Applied, applied.Error);
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE generation=1"));
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id='" + build + "' AND attempt=1"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_applied'"));
        using var snapshot = f.Core.Db.OpenSnapshot(default);
        var redo = Assembly.Engine.Workflows.Reviews.ReviewTransitions.RedoValues(snapshot, f.Run, f.Step, 1)!;
        Assert.Equal(build, redo.TargetStepId);
        Assert.Equal("exit 7", redo.Values.GetProperty("findings")[0].GetString());
        Assert.Contains("collected", redo.Values.GetProperty("output").GetString());
        Assert.NotNull(redo.OutputReceiptId);
        Assert.Equal(64, redo.SourceDigest.Length);
        Assert.Null(Assembly.Engine.Workflows.Reviews.ReviewTransitions.RedoValues(snapshot, f.Run, f.Step, 2));
    }

    [Fact]
    public async Task Shared_list_deadline_stops_remaining_commands_and_requires_explicit_new_attempt()
    {
        using var f = new Fixture("printf first", "printf second", "printf must-not-run");
        Assert.True(f.Start(f.Prepare()).Applied);
        var deadline = f.Core.Number("SELECT deadline_ms FROM engine_step");
        var first = f.Claim(); Assert.True(f.Complete(first, await f.Host().ExecuteAsync(first, default)).Applied);
        Assert.True(f.Start(f.Prepare(1500)).Applied);
        Assert.Equal(deadline, f.Core.Number("SELECT deadline_ms FROM engine_step"));
        var second = f.Claim(); Assert.True(f.Complete(second, await f.Host().ExecuteAsync(second, default)).Applied);
        var c = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.System, "engine.worker"), f.Step,
            f.Core.Number("SELECT version FROM engine_step"), 1, deadline);
        Assert.Equal("not_due", f.Core.Db.Writer.Write("test.early-timeout", scope => f.Commands.ExpireWorkflowCommand(scope, c with { CommandId = Guid.NewGuid(), NowMs = deadline - 1 })).Error);
        Assert.True(f.Core.Db.Writer.Write("test.timeout", scope => f.Commands.ExpireWorkflowCommand(scope, c)).Applied);
        Assert.Equal("timed_out", f.Core.Text("SELECT outcome_reason FROM engine_step"));
        Assert.Equal("waiting", f.Core.Text("SELECT state FROM engine_step"));
        Assert.Equal("settled", f.Core.Text("SELECT gate_state FROM engine_step_attempt"));
        Assert.Equal("command_timeout", f.Core.Text("SELECT kind FROM engine_needs_me WHERE state='open'"));
        Assert.Equal("needs_me", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Throws<InvalidOperationException>(() => f.Prepare(deadline + 1));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command'"));
        var need = f.Core.Text("SELECT id FROM engine_needs_me WHERE state='open'")!;
        var retry = c with { CommandId = Guid.NewGuid(), EntityId = f.Run, ExpectedVersion = f.Core.Number("SELECT version FROM engine_run"), Epoch = 0, Actor = new(ActorKind.Window, "owner") };
        Assert.True(f.Core.Apply(retry, new Assembly.Engine.Workflows.Runs.RetryWorkflowStep(f.Step)).Applied);
        Assert.Equal("answered", f.Core.State("engine_needs_me", need));
        Assert.Equal(1, f.Core.Number("SELECT generation FROM engine_step"));
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_run"));
        Assert.True(f.Start(f.Prepare(deadline + 2)).Applied);
        Assert.Equal(2, f.Core.Number("SELECT attempt FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT command_index FROM engine_step_attempt WHERE attempt=2"));
        Assert.Equal("timed_out", f.Core.Text("SELECT reason FROM engine_step_attempt WHERE attempt=1"));
    }

    [Fact]
    public void Prepared_command_identity_ignores_clock_and_never_starts_twice()
    {
        using var f = new Fixture("printf once");
        var first = f.Prepare(); var nextClock = f.Prepare(2000);
        Assert.Equal(first.Context.CommandId, nextClock.Context.CommandId);
        var accepted = f.Start(first); Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal(accepted, f.Start(nextClock));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command'"));
        Assert.Equal(1, f.Core.Count("engine_step_attempt"));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _temp = Directory.CreateTempSubdirectory("assembly-command-engine-").FullName;
        public EngineFixture Core { get; }
        public EngineCommands Commands => new(Core.Machine, Core.Limits);
        public string Run { get; } = Guid.NewGuid().ToString("N");
        public string Step => Core.Text("SELECT id FROM engine_step WHERE definition_key='checks'")!;
        private string Managed => Path.Combine(_temp, "managed");
        private string Proof => Path.Combine(_temp, "proof");
        public string ProofRoot => Proof;
        public Fixture(params string[] scripts) : this(scripts, "allow") { }
        public Fixture(string[] scripts, string policy, bool onFail = false, bool repeat = false, Assembly.Application.Common.Topics.ITopics? topics = null)
        {
            Core = new(topics: topics);
            Core.SeedSession("owner"); var root = Path.Combine(_temp, "repo"); Directory.CreateDirectory(root);
            var store = new ProjectStore(Core.Db, Core.Time);
            var project = Core.Db.Writer.Write("test.project", s => store.AddOrRestore(s, Core.Machine, root, "Fixture", null).Project);
            var request = new WorkflowStartRequest(Guid.NewGuid(), Run, 0, 0, "owner", "request", ".assembly/workflows/flow.yml", project.Id,
                "commands", null, JsonSerializer.SerializeToElement(new { }));
            var c = new CommandContext(request.CommandId, Core.Machine, new(ActorKind.Window, "owner"), Run, 0, 0, 1000);
            var yaml = "name: Flow\npolicy:\n  commands:\n    " + policy + ": ['*']\nsteps:\n"
                + (onFail ? "  build:\n    run: [printf built]\n" : "")
                + "  checks:\n" + (onFail ? "    needs: [build]\n    on_fail: build\n" : "")
                + (repeat ? "    repeat: true\n" : "") + "    run: " + JsonSerializer.Serialize(scripts) + "\n";
            var snapshot = WorkflowParser.Parse(new(yaml, ImmutableSortedDictionary<string, string>.Empty));
            var prepared = WorkflowStarts.Prepare(c, request, snapshot, project);
            Assert.True(Core.Db.Writer.Write("test.run", s => WorkflowStarts.Apply(s, Commands, c, prepared)).Result.Applied);
            var setup = Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_setup'")!;
            Assert.True(Core.Apply(new ClaimEffect(), setup, actor: new(ActorKind.System, "engine.worker")).Applied);
            var anchor = Workspace(null);
            Assert.True(Complete(EffectTransitions.Read(Core.Db, setup, default)!, new(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new WorkspaceSetupData(anchor.AllocationId), EngineJson.Default.WorkspaceSetupData), null)).Applied);
            CompleteWorkspace(anchor);
            CompleteWorkspace(Workspace(onFail ? Core.Text("SELECT id FROM engine_step WHERE definition_key='build'")! : Step));
        }
        private WorkspaceIntent Workspace(string? step)
        {
            PreparedWorkflowCommand prepared;
            using (var scope = Core.Db.OpenSnapshot(default))
            {
                var c = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "workflow.workspace"), Run,
                    Core.Number("SELECT version FROM engine_run"), 0, 1000);
                prepared = WorkspacePreparation.Prepare(scope, c, new(step, 0, Guid.NewGuid().ToString("N"), new string('a', 40), "new"), Managed, Proof);
            }
            Assert.True(Core.Db.Writer.Write("test.workspace", s => Commands.ApplyPreparedWorkflow(s, prepared.Workspace!.Context with { NowMs = 1000 }, prepared)).Applied);
            Directory.CreateDirectory(prepared.Workspace!.Intent.Path);
            Directory.CreateDirectory(prepared.Workspace.Intent.ProofDir);
            File.WriteAllText(Path.Combine(prepared.Workspace.Intent.ProofDir, ".assembly-workflow-allocation"), Run);
            return prepared.Workspace.Intent;
        }
        private void CompleteWorkspace(WorkspaceIntent workspace)
        {
            var id = Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_workspace' AND json_extract(payload_json,'$.allocationId')='" + workspace.AllocationId + "'")!;
            Assert.True(Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
            Assert.True(Complete(EffectTransitions.Read(Core.Db, id, default)!, new(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new WorkspacePrepared(workspace.AllocationId, workspace.Path, workspace.Branch, workspace.BaseHead, workspace.BaseHead, null),
                    EngineJson.Default.WorkspacePrepared), null)).Applied);
        }
        public async Task ReadyBuildAsync()
        {
            var build = Core.Text("SELECT id FROM engine_step WHERE definition_key='build'")!;
            Assert.True(Start(PrepareFor(build, 1000)).Applied);
            var effect = Claim(); Assert.True(Complete(effect, await Host().ExecuteAsync(effect, default)).Applied);
            CompleteWorkspace(Workspace(Step));
        }
        public PreparedCommandStep Prepare(long now = 1000) => PrepareFor(Step, now);
        private PreparedCommandStep PrepareFor(string step, long now)
        {
            using var scope = Core.Db.OpenSnapshot(default);
            var c = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "workflow.command"), step,
                Core.Number("SELECT version FROM engine_step WHERE id='" + step + "'"), 0, now);
            return CommandStepPreparation.Prepare(scope, c, "tree");
        }
        public CommandResult Start(PreparedCommandStep prepared)
        {
            WorkflowPrBodyFile.WriteAsync(prepared.PrBody, Proof, Run, default).GetAwaiter().GetResult();
            return Core.Db.Writer.Write("test.command", s => Commands.ApplyPreparedCommandStep(s, prepared.Context, prepared));
        }
        public Effect Claim()
        {
            var id = Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_command' AND state IN ('pending','uncertain') ORDER BY rowid LIMIT 1")!;
            Assert.True(Core.Apply(new ClaimEffect(), id, Core.Number("SELECT version FROM engine_outbox WHERE id='" + id + "'"), actor: new(ActorKind.System, "engine.worker")).Applied);
            return EffectTransitions.Read(Core.Db, id, default)!;
        }
        public CommandStepEffects Host(string fingerprint = "tree") => new(Core.Db, Commands, Core.Machine, Proof, Core.Time, (_, _) => Task.FromResult(fingerprint));
        public CommandResult Complete(Effect effect, EffectReceipt receipt) => Core.Apply(new CompleteEffect(receipt), effect.Id,
            Core.Number("SELECT version FROM engine_outbox WHERE id='" + effect.Id + "'"), actor: new(ActorKind.System, "engine.worker"));
        public void Dispose() { Core.Dispose(); Directory.Delete(_temp, true); }
    }
}
