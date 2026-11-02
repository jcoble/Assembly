using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Workspaces;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Infrastructure.Git;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Workflows.Workspaces;

public sealed class WorkflowWorkspaceTests
{
    [Fact]
    public void Taskless_branch_and_proof_intent_are_durable_before_io_and_replay_once()
    {
        using var f = new Fixture(); var prepared = f.Prepare(); var result = f.Apply(prepared);
        Assert.True(result.Applied, result.Error); var i = prepared.Workspace!.Intent;
        Assert.Equal("run-" + f.Run[..8], i.Branch);
        Assert.Equal(i.Path, f.Core.Text("SELECT worktree FROM engine_run"));
        Assert.Equal(i.Branch, f.Core.Text("SELECT branch FROM engine_run"));
        Assert.Equal(Path.Combine(f.Proof, i.AllocationId), i.ProofDir);
        Assert.False(Directory.Exists(i.Path)); Assert.False(Directory.Exists(i.ProofDir));
        Assert.Equal(i.ProofDir, f.Core.Text("SELECT proof_dir FROM engine_run"));
        Assert.Equal(Path.Combine(i.ProofDir, "pr-body.md"), f.Core.Text("SELECT pr_body FROM engine_run"));
        f.Core.Reopen(); Assert.Equal(result, f.Apply(prepared));
        Assert.Equal(1, f.Core.Count("engine_workflow_workspace"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_workspace'"));
    }

    [Fact]
    public async Task Setup_failure_is_visible_without_starting_a_session()
    {
        using var f = new Fixture();
        // The isolated project directory is deliberately not a Git repository.
        var host = new WorkspaceEffects(f.Core.Db, f.Commands, f.Core.Machine, f.Proof,
            new WorkflowWorkspace(f.Proof), f.Core.Time);
        var receipt = await host.ExecuteAsync(f.Setup, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.NotNull(receipt.Data.Deserialize(EngineJson.Default.WorkspaceSetupData)!.Error);
        Assert.True(f.Complete(f.Setup, receipt).Applied);
        Assert.Equal("failed", f.Core.Text("SELECT setup_state FROM engine_run"));
        Assert.Equal(1, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='setup_failed'"));
    }

    [Fact]
    public void Workspace_confirmation_is_bound_and_sql_failure_rolls_back_receipt_and_run()
    {
        using var f = new Fixture(); var p = f.Prepare(); Assert.True(f.Apply(p).Applied);
        var effect = f.ClaimWorkspace(); var i = p.Workspace!.Intent;
        var result = new WorkspacePrepared(i.AllocationId, i.Path, i.Branch, i.BaseHead, i.BaseHead, null);
        Assert.Equal("bad_receipt", f.Complete(effect, Receipt(result with { Path = i.Path + "-wrong" })).Error);
        f.Core.Db.Writer.Write("test.reject-workspace-history", s => s.Execute("""
            CREATE TRIGGER reject_workspace_history BEFORE INSERT ON engine_workflow_history
            WHEN NEW.kind='workspace_ready' BEGIN SELECT RAISE(ABORT,'workspace history fault'); END;
            """));
        Assert.Throws<SqliteException>(() => f.Complete(effect, Receipt(result)));
        Assert.Equal("inflight", f.Core.Text("SELECT state FROM engine_outbox WHERE kind='workflow_workspace'"));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_workflow_workspace"));
        Assert.Equal("pending", f.Core.Text("SELECT setup_state FROM engine_run"));
        f.Core.Db.Writer.Write("test.allow-workspace-history", s => s.Execute("DROP TRIGGER reject_workspace_history"));
        Assert.True(f.Complete(effect, Receipt(result)).Applied);
        Assert.Equal("ready", f.Core.Text("SELECT setup_state FROM engine_run"));
        Assert.Equal(i.BaseHead, f.Core.Text("SELECT work_head FROM engine_run"));
    }

    [Fact]
    public void Owner_reservation_after_preparation_refuses_all_allocation_writes()
    {
        using var f = new Fixture(); var p = f.Prepare();
        f.Core.Db.Writer.Write("test.reserve-owner", s => s.Execute("UPDATE engine_session SET reserved_epoch=1 WHERE id='owner'"));
        Assert.Equal("owner_reserved", f.Apply(p).Error);
        Assert.Equal(0, f.Core.Count("engine_workflow_workspace"));
        Assert.Null(f.Core.Text("SELECT proof_dir FROM engine_run"));
    }

    [Fact]
    public async Task Workspace_effect_bridge_reopens_and_reuses_real_git_allocation()
    {
        using var f = new Fixture(); var git = new GitCli();
        async Task Git(params string[] args) { await foreach (var unused in git.ReadAsync(f.Root, args, null, default)) { } }
        await Git("init"); await Git("config", "user.name", "Workflow Fixture"); await Git("config", "user.email", "workflow@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "base"), "base"); await Git("add", "."); await Git("commit", "-m", "base");
        WorkspaceEffects Host() => new(f.Core.Db, f.Commands, f.Core.Machine, f.Proof,
            new WorkflowWorkspace(f.Proof), f.Core.Time);
        var first = await Host().ExecuteAsync(f.Setup, default);
        var again = await Host().ReconcileAsync(f.Setup, default);
        Assert.Equal(first.Data.GetRawText(), again.Data.GetRawText());
        Assert.True(f.Complete(f.Setup, first).Applied);
        var effect = f.ClaimWorkspace(); var done = await Host().ExecuteAsync(effect, default);
        Assert.Null(done.Data.Deserialize(EngineJson.Default.WorkspacePrepared)!.Error);
        f.Core.Reopen(); var replay = await Host().ReconcileAsync(effect, default);
        Assert.Equal(done.Data.GetRawText(), replay.Data.GetRawText());
        Assert.True(f.Complete(effect, replay).Applied);
        Assert.Equal("ready", f.Core.Text("SELECT setup_state FROM engine_run"));
        Assert.Equal(1, f.Core.Count("engine_workflow_workspace"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_workspace' AND state='done'"));
    }

    [Fact]
    public void New_generation_never_borrows_previous_workspace()
    {
        using var f = new Fixture(); f.ReadyAnchor();
        var step = f.Core.Text("SELECT id FROM engine_step WHERE definition_key='build'")!;
        var first = f.Prepare(step); Assert.True(f.Apply(first).Applied);
        var effect = f.ClaimWorkspace(first.Workspace!.Intent.AllocationId);
        var i = first.Workspace.Intent;
        Assert.True(f.Complete(effect, Receipt(new(i.AllocationId, i.Path, i.Branch, i.BaseHead, i.BaseHead, null))).Applied);
        // Closed state fixture for Task9's future generation transition; no provider or redo producer is claimed.
        f.Core.Db.Writer.Write("test.next-generation", scope => scope.Execute("UPDATE engine_step SET generation=1 WHERE id=$id", new SqlArg("$id", step)));
        var next = f.Prepare(step, 1); Assert.True(f.Apply(next).Applied);
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_workspace WHERE step_id IS NOT NULL"));
        Assert.NotEqual(i.Path, f.Core.Text("SELECT path FROM engine_workflow_workspace WHERE allocation_id='" + next.Workspace!.Intent.AllocationId + "'"));
    }

    [Fact]
    public void Pending_dependency_and_stale_head_refuse_before_allocating_step_checkout()
    {
        using var f = new Fixture(); f.ReadyAnchor();
        var step = f.Core.Text("SELECT id FROM engine_step WHERE definition_key='checks'")!;
        Assert.Equal("workspace_not_ready", f.Apply(f.Prepare(step)).Error);
        // Only the stored trusted-head consumer is tested; Task7's external receipt-check producer remains separate.
        f.Core.Db.Writer.Write("test.checked-build", scope => scope.Execute("""
            UPDATE engine_step SET state='succeeded' WHERE definition_key='build';
            UPDATE engine_run SET work_head=$head;
            """, new SqlArg("$head", new string('b', 40))));
        Assert.Equal("workspace_not_ready", f.Apply(f.Prepare(step)).Error);
        var ready = f.Prepare(step, head: new string('b', 40)); Assert.True(f.Apply(ready).Applied);
        Assert.Equal(new string('b', 40), ready.Workspace!.Intent.BaseHead);
        Assert.Equal(2, f.Core.Number("SELECT version FROM engine_step WHERE definition_key='checks'"));
        Assert.Equal(1000, f.Core.Number("SELECT updated_ms FROM engine_step WHERE definition_key='checks'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='step.changed' AND entity_id='" + step + "'"));
    }

    private static EffectReceipt Receipt(WorkspacePrepared value) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(value, EngineJson.Default.WorkspacePrepared), null);

    private sealed class Fixture : IDisposable
    {
        private readonly string _temp = Directory.CreateTempSubdirectory("a40-workspace-engine-").FullName;
        public EngineFixture Core { get; } = new();
        public string Root => Path.Combine(_temp, "repo");
        public string Managed => Path.Combine(_temp, "worktrees");
        public string Proof => Path.Combine(_temp, "proof");
        public string Run { get; } = Guid.NewGuid().ToString("N");
        public Effect Setup { get; }
        public EngineCommands Commands => new(Core.Machine, Core.Limits);
        public Fixture()
        {
            Directory.CreateDirectory(Root); Core.SeedSession("owner");
            var store = new ProjectStore(Core.Db, Core.Time);
            var project = Core.Db.Writer.Write("test.project", s => store.AddOrRestore(s, Core.Machine, Root, "Fixture", null).Project);
            var request = new WorkflowStartRequest(Guid.NewGuid(), Run, 0, 0, "owner", "key", ".assembly/workflows/flow.yml",
                project.Id, "do work", null, JsonSerializer.SerializeToElement(new { }));
            var context = new CommandContext(request.CommandId, Core.Machine, new(ActorKind.Window, "owner"), Run, 0, 0, 1000);
            var snapshot = WorkflowParser.Parse(new("name: Flow\nsteps:\n  build:\n    agent: {provider: codex}\n    prompt: prompt.md\n    result: build\n  checks:\n    needs: [build]\n    run: [printf ok]",
                ImmutableSortedDictionary<string, string>.Empty.Add("prompt.md", "work")));
            var prepared = WorkflowStarts.Prepare(context, request, snapshot, project);
            Assert.True(Core.Db.Writer.Write("test.start", s => WorkflowStarts.Apply(s, Commands, context, prepared)).Result.Applied);
            var id = Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_setup'")!;
            Assert.True(Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
            Setup = EffectTransitions.Read(Core.Db, id, default)!;
        }
        public PreparedWorkflowCommand Prepare(string? step = null, int generation = 0, string? head = null)
        {
            using var scope = Core.Db.OpenSnapshot(default);
            var context = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "workflow.workspace"), Run,
                Core.Number("SELECT version FROM engine_run"), 0, 1000);
            return WorkspacePreparation.Prepare(scope, context, new(step, generation, Guid.NewGuid().ToString("N"), head ?? new string('a', 40), "new"), Managed, Proof);
        }
        public CommandResult Apply(PreparedWorkflowCommand p) => Core.Db.Writer.Write("test.workspace", s => Commands.ApplyPreparedWorkflow(s, p.Workspace!.Context with { NowMs = 1000 }, p));
        public Effect ClaimWorkspace(string? allocation = null)
        {
            var id = Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_workspace'" + (allocation is null ? "" : " AND json_extract(payload_json,'$.allocationId')='" + allocation + "'"))!;
            Assert.True(Core.Apply(new ClaimEffect(), id, actor: new(ActorKind.System, "engine.worker")).Applied);
            return EffectTransitions.Read(Core.Db, id, default)!;
        }
        public void ReadyAnchor()
        {
            var prepared = Prepare(); Assert.True(Apply(prepared).Applied);
            var i = prepared.Workspace!.Intent;
            Assert.True(Complete(ClaimWorkspace(), Receipt(new(i.AllocationId, i.Path, i.Branch, i.BaseHead, i.BaseHead, null))).Applied);
        }
        public CommandResult Complete(Effect e, EffectReceipt receipt) => Core.Apply(new CompleteEffect(receipt), e.Id,
            Core.Number("SELECT version FROM engine_outbox WHERE id='" + e.Id + "'"), actor: new(ActorKind.System, "engine.worker"));
        public void Dispose() { Core.Dispose(); Directory.Delete(_temp, true); }
    }
}
