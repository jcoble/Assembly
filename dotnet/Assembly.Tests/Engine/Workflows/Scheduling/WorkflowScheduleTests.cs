using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Scheduling;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Engine.Workflows.Scheduling;

public sealed class WorkflowScheduleTests
{
    [Fact]
    public void Worker_is_direct_child_with_definition_provider_model_and_full_prompt()
    {
        using var f = new Fixture(new string('x', 40000) + "🙂tail");
        var prepared = f.Prepare(); var ack = f.Apply(prepared);
        Assert.True(ack.Result.Applied, ack.Result.Error);
        Assert.Equal("owner", f.Core.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", ack.SessionId)));
        Assert.Equal(f.Run, f.Core.Text("SELECT run_id FROM engine_session WHERE id=$id", new SqlArg("$id", ack.SessionId)));
        Assert.Equal("codex", f.Core.Text("SELECT provider FROM engine_session WHERE id=$id", new SqlArg("$id", ack.SessionId)));
        Assert.Equal("selected-model", f.Core.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", ack.SessionId)));
        Assert.Equal(f.Prompt, f.Core.Text("SELECT text FROM engine_message WHERE id=$id", new SqlArg("$id", ack.MessageId)));
        Assert.Equal(new string('b', 64), f.Core.Text("SELECT fingerprint FROM engine_step_attempt"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'")); // readiness owns release
    }

    [Fact]
    public void Two_wakes_and_reopen_replay_one_attempt_one_child_one_prompt()
    {
        using var f = new Fixture(); var prepared = f.Prepare(); var first = f.Apply(prepared);
        Assert.True(first.Result.Applied, first.Result.Error);
        f.Core.Reopen(); Assert.Equal(first, f.Apply(prepared));
        Assert.Equal(1, f.Core.Count("engine_step_attempt")); Assert.Equal(2, f.Core.Count("engine_session"));
        Assert.Equal(1, f.Core.Count("engine_message"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='dispatch_intent'"));
    }

    [Fact]
    public void Free_cap_refusal_recovery_replays_original_intent_and_uses_one_attempt()
    {
        using var f = new Fixture(); var original = f.Prepare();
        for (var i = 0; i < 5; i++) f.Core.SeedSession("occupied-" + i, parentId: "owner");
        var refused = f.Apply(original); Assert.False(refused.Result.Applied); Assert.Equal("child_limit", refused.Result.Error);
        Assert.Null(refused.SessionId); Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        var receipt = f.Core.Text("SELECT fingerprint FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", original.Context.CommandId.ToString("N")));
        f.Core.Db.Writer.Write("test.cap-release", s => s.Execute("UPDATE engine_session SET state='ended' WHERE parent_id='owner'"));
        f.Core.Reopen(); Assert.Equal(refused, f.Apply(original));
        var next = f.Prepare(); Assert.NotEqual(original.Context.CommandId, next.Context.CommandId);
        Assert.Equal(original.Body.Prepared.Hash, next.Body.Prepared.Hash); // exact ABA restored eligibility
        var competing = f.Prepare(); Assert.Equal(next.Context.CommandId, competing.Context.CommandId);
        var accepted = f.Apply(next); Assert.True(accepted.Result.Applied, accepted.Result.Error); Assert.Equal(accepted, f.Apply(competing));
        Assert.Equal(refused, f.Apply(original)); Assert.Equal(1, f.Core.Count("engine_step_attempt"));
        Assert.Equal(receipt, f.Core.Text("SELECT fingerprint FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", original.Context.CommandId.ToString("N"))));
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='dispatch_intent'"));
    }

    [Fact]
    public void Later_message_refusal_rolls_back_shell_attempt_effects_and_publications()
    {
        using var f = new Fixture(messageLimit: 0); var prepared = f.Prepare(); var before = f.Topics.Count;
        var ack = f.Apply(prepared); Assert.False(ack.Result.Applied); Assert.Equal("message_limit", ack.Result.Error);
        Assert.Equal(1, f.Core.Count("engine_session")); Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal(before, f.Topics.Count);
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='dispatch_intent'"));
        Assert.Equal("pending", f.Core.State("engine_step", f.Step));
    }

    [Fact]
    public void Unexpected_projection_failure_rolls_back_root_receipt_and_intent()
    {
        using var f = new Fixture(failProjection: true); var prepared = f.Prepare(); var before = f.Core.Count("engine_command_receipt");
        Assert.Throws<InvalidOperationException>(() => f.Apply(prepared));
        Assert.Equal(before, f.Core.Count("engine_command_receipt")); Assert.Equal(1, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='dispatch_intent'"));
    }

    [Fact]
    public void Bare_workflow_success_requires_checked_receipt()
    {
        using var f = new Fixture(); var accepted = f.Apply(f.Prepare()); Assert.True(accepted.Result.Applied);
        var version = f.Core.Number("SELECT version FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step));
        var result = f.Core.Apply(new Assembly.Engine.Runs.SetStepState("succeeded", 1, ["https://example.com/proof"], null, false), f.Step, version, 0, new(ActorKind.Session, accepted.SessionId!));
        Assert.Equal("receipt_required", result.Error); Assert.Equal("running", f.Core.State("engine_step", f.Step));
    }

    [Fact]
    public void Same_intent_changed_full_prompt_conflicts_without_mutation()
    {
        using var f = new Fixture(); var original = f.Prepare(); var first = f.Apply(original); Assert.True(first.Result.Applied);
        var frozen = WorkflowPreparation.Freeze(original.Body with { Prompt = "another prompt of changed content" }, default);
        var changed = new PreparedWorkflowDispatch(frozen.Body, frozen.Envelope, f.Commands);
        var conflict = f.Apply(changed); Assert.Equal("command_id_conflict", conflict.Result.Error);
        Assert.Equal(1, f.Core.Count("engine_step_attempt")); Assert.Equal(f.Prompt, f.Core.Text("SELECT text FROM engine_message"));
    }

    [Fact]
    public void Missing_root_receipt_blocks_replay_without_recreating_execution()
    {
        using var f = new Fixture(); var original = f.Prepare(); Assert.True(f.Apply(original).Result.Applied);
        f.Core.Db.Writer.Write("test.corrupt-receipt", s => s.Execute("DELETE FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", original.Context.CommandId.ToString("N"))));
        Assert.Throws<InvalidOperationException>(() => f.Apply(original)); Assert.Equal(1, f.Core.Count("engine_step_attempt"));
        Assert.Equal(1, f.Core.Count("engine_message"));
    }

    [Fact]
    public void History_failure_rolls_back_root_receipt_and_complete_stage_state()
    {
        using var f = new Fixture(); var original = f.Prepare(); var before = f.Core.Count("engine_command_receipt");
        f.Core.Db.Writer.Write("test.history-failure", s => s.Execute("CREATE TRIGGER fail_dispatch_history BEFORE INSERT ON engine_workflow_history WHEN NEW.kind='dispatch_intent' BEGIN SELECT RAISE(ABORT,'history failure'); END"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Apply(original));
        Assert.Equal(before, f.Core.Count("engine_command_receipt")); Assert.Equal(1, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Count("engine_step_attempt"));
    }

    [Fact]
    public void Start_preserves_authored_definition_order_without_changing_snapshot_shape()
    {
        using var f = new Fixture(yaml: """
            name: Flow
            steps:
              build:
                agent: {provider: codex, model: selected-model}
                prompt: prompt.md
                result: build
              areview:
                needs: [build]
                review: {of: build, agent: {provider: codex}, prompt: prompt.md}
                result: review
            """);
        var order = f.Core.Db.Query("SELECT definition_key FROM engine_step ORDER BY rowid", r => r.Text(0), default);
        Assert.Equal(new[] { "build", "areview" }, order);
    }

    [Fact]
    public void Late_workflow_fact_preserves_archived_attempt_outcome()
    {
        using var f = new Fixture(); var accepted = f.Apply(f.Prepare()); Assert.True(accepted.Result.Applied);
        var version = f.Core.Number("SELECT version FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step));
        Assert.True(f.Core.Apply(new Assembly.Engine.Runs.SetStepState("failed", 1, [], "first failure", false), f.Step, version, 0, new(ActorKind.Session, accepted.SessionId!)).Applied);
        var original = f.Core.Text("SELECT reason FROM engine_step_attempt");
        Assert.True(f.Core.Apply(new Assembly.Engine.Runs.SetStepState("failed", 1, [], "late failure", false), f.Step, 0, 0, new(ActorKind.System, "engine.feed")).Late);
        Assert.Equal(original, f.Core.Text("SELECT reason FROM engine_step_attempt"));
        Assert.Equal(1, f.Core.Count("engine_step_fact")); Assert.Equal(0, f.Core.Number("SELECT late FROM engine_step_attempt"));
    }

    [Fact]
    public void Two_wakes_at_different_times_converge_on_original_immutable_intent()
    {
        using var f = new Fixture(); var earlier = f.Prepare(1000); var later = f.Prepare(1009);
        Assert.Equal(earlier.Context.CommandId, later.Context.CommandId);
        Assert.Equal(earlier.Envelope.Sha256, later.Envelope.Sha256);
        var first = f.Apply(earlier); Assert.True(first.Result.Applied, first.Result.Error);
        Assert.Equal(first, f.Apply(later)); Assert.Equal(1, f.Core.Count("engine_step_attempt"));
        Assert.Equal(1000, f.Core.Number("SELECT started_ms FROM engine_step_attempt"));
    }

    [Fact]
    public void Provider_auth_race_records_blocked_witness_and_recovers_without_consuming_attempt()
    {
        using var f = new Fixture(); var original = f.Prepare();
        f.Core.Db.Writer.Write("test.auth-block", s => s.Execute("UPDATE engine_provider SET auth_state='needs_sign_in'"));
        var refused = f.Apply(original); Assert.Equal("sign_in_required", refused.Result.Error);
        Assert.Equal(0, f.Core.Count("engine_step_attempt"));
        f.Core.Db.Writer.Write("test.auth-recover", s => s.Execute("UPDATE engine_provider SET auth_state='signed_in'"));
        var restored = f.Prepare(); Assert.Equal(original.Body.Prepared.Hash, restored.Body.Prepared.Hash);
        Assert.NotEqual(original.Context.CommandId, restored.Context.CommandId);
        Assert.True(f.Apply(restored).Result.Applied); Assert.Equal(refused, f.Apply(original));
        Assert.Equal(1, f.Core.Count("engine_step_attempt"));
    }

    [Fact]
    public void Refused_stage_discards_after_commit_state_and_success_publishes_it_once()
    {
        using var refused = new Fixture(messageLimit: 0);
        var failure = refused.Apply(refused.Prepare());
        Assert.Equal("message_limit", failure.Result.Error);
        Assert.Equal(0, refused.CommittedShells);
        Assert.Equal(0, refused.Core.Count("engine_step_attempt"));
        using var success = new Fixture();
        var prepared = success.Prepare();
        Assert.True(success.Apply(prepared).Result.Applied);
        Assert.Equal(1, success.CommittedShells);
        Assert.Equal(1, success.Core.Count("engine_message"));
        Assert.True(success.Apply(prepared).Result.Applied);
        Assert.Equal(1, success.CommittedShells);
    }

    [Fact]
    public async Task Same_host_callback_prepares_command_once_without_provider_session()
    {
        var root = Directory.CreateTempSubdirectory("workflow-command-callback-").FullName;
        var proofRoot = Directory.CreateTempSubdirectory("workflow-command-proof-").FullName;
        try
        {
            var git = new GitCli();
            async Task RunGit(params string[] args)
            { await foreach (var value in git.ReadAsync(root, args, null, default)) { _ = value; } }
            await RunGit("init");
            await RunGit("config", "user.name", "Workflow Callback Fixture");
            await RunGit("config", "user.email", "workflow.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "checked source");
            await RunGit("add", "."); await RunGit("commit", "-m", "fixture");
            var checkout = await new ReceiptCheckout().Capture(root, default);
            using var f = new Fixture(yaml: """
                name: Commands
                policy: {commands: {allow: ["*"]}, default: deny}
                steps:
                  build:
                    run: ["printf checked"]
                """);
            var proof = Path.Combine(proofRoot, f.Run);
            Directory.CreateDirectory(proof);
            await File.WriteAllTextAsync(Path.Combine(proof, ".assembly-workflow-allocation"), f.Run);
            f.Core.Db.Writer.Write("test.callback-workspace", s => s.Execute("""
                UPDATE engine_workflow_workspace SET path=$folder,head=$head,base_head=$head;
                UPDATE engine_run SET proof_dir=$proof,pr_body=$body,work_head=$head WHERE id=$run;
                """, new("$folder", root), new("$head", checkout.Head), new("$proof", proof),
                new("$body", Path.Combine(proof, "pr-body.md")), new("$run", f.Run)));
            var callback = new WorkflowDispatchPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time, Path.Combine(root, "managed"), proofRoot);
            Assert.Empty(await callback.PrepareAsync(default));
            Assert.True(File.Exists(Path.Combine(proof, "pr-body.md")));
            Assert.Equal("running", f.Core.State("engine_step", f.Step));
            Assert.Equal(1, f.Core.Count("engine_step_attempt")); Assert.Equal(1, f.Core.Count("engine_session"));
            Assert.Equal(checkout.Fingerprint, f.Core.Text("SELECT fingerprint FROM engine_step_attempt"));
            Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command'"));
            Assert.Empty(await callback.PrepareAsync(default));
            Assert.Equal(1, f.Core.Count("engine_step_attempt")); Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='workflow_command'"));
        }
        finally { Directory.Delete(root, true); Directory.Delete(proofRoot, true); }
    }

    [Fact]
    public void Redo_reviewer_gets_fresh_session_even_with_retained_old_authority()
    {
        using var f = new Fixture(yaml: """
            name: Review
            steps:
              target:
                agent: {provider: codex, model: selected-model}
                prompt: prompt.md
                result: build
              build:
                needs: [target]
                review: {of: target, agent: {provider: codex, model: selected-model}, prompt: prompt.md}
                result: review
            """);
        f.Core.Db.Writer.Write("test.accepted-dependency", s => s.Execute("""
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,epoch,hash,kind,body_json,state)
              SELECT 'dependency-receipt',1,machine,1000,1000,run_id,id,0,0,0,'dependency-hash','build',
                '{"report":{"receipt":{"kind":"build","summary":"checked target"}}}','accepted'
              FROM engine_step WHERE run_id=$run AND definition_key='target';
            UPDATE engine_step SET state='succeeded',receipt_id='dependency-receipt' WHERE run_id=$run AND definition_key='target';
            """, new SqlArg("$run", f.Run)));
        var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied, first.Result.Error);
        PrepareReleasedRedo(f, first.SessionId!);
        f.Core.Db.Writer.Write("test.fresh-reviewer", s => s.Execute("UPDATE engine_step SET session_id=NULL WHERE id=$step", new SqlArg("$step", f.Step)));
        var prepared = f.Prepare(2000); Assert.NotNull(prepared); Assert.Null(prepared.Body.Prepared.Witness.Resume);
        var second = f.Apply(prepared); Assert.True(second.Result.Applied, second.Result.Error);
        Assert.NotEqual(first.SessionId, second.SessionId); Assert.Equal(3, f.Core.Count("engine_session"));
        Assert.Equal("native", f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id=$id", new SqlArg("$id", first.SessionId)));
        Assert.Null(f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id=$id", new SqlArg("$id", second.SessionId)));
        Assert.Equal(0, f.Core.Number("SELECT execution_epoch FROM engine_step_attempt WHERE step_id=$step AND attempt=2", new SqlArg("$step", f.Step)));
        Assert.Equal(2, f.CommittedShells);
    }

    [Fact]
    public void Redo_resumes_original_worker_with_new_epoch_and_preserves_archived_attempt()
    {
        using var f = new Fixture();
        f.Core.Db.Writer.Write("test.owner-epoch", s => s.Execute("UPDATE engine_session SET epoch=7 WHERE id='owner'"));
        var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied);
        PrepareReleasedRedo(f, first.SessionId!);
        var archived = f.Core.Text("SELECT json_object('state',state,'receipt',receipt_id,'epoch',execution_epoch,'prompt',prompt_message_id) FROM engine_step_attempt WHERE attempt=1");
        var prepared = f.Prepare(2000);
        Assert.True(f.Core.Apply(new Assembly.Engine.Sessions.ReportSessionTitle("changed display"), first.SessionId!, 2, 0, new(ActorKind.System, "engine.feed")).Applied);
        var competing = f.Prepare(2009);
        Assert.NotNull(prepared); Assert.Equal(first.SessionId, prepared.Body.SessionId);
        Assert.Equal(prepared.Context.CommandId, competing.Context.CommandId);
        Assert.Equal(prepared.Envelope.Sha256, competing.Envelope.Sha256);
        Assert.Equal(7, prepared.Context.Epoch);
        Assert.Equal(0, prepared.Body.Prepared.Witness.Resume!.Epoch);
        var resumed = f.Apply(prepared); Assert.True(resumed.Result.Applied, resumed.Result.Error);
        Assert.Equal(resumed, f.Apply(competing));
        Assert.Equal(first.SessionId, resumed.SessionId);
        Assert.Equal(2, f.Core.Count("engine_session")); Assert.Equal(2, f.Core.Count("engine_step_attempt"));
        Assert.Equal("native", f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id=$id", new SqlArg("$id", resumed.SessionId)));
        Assert.Equal(1, f.Core.Number("SELECT epoch FROM engine_session WHERE id=$id", new SqlArg("$id", resumed.SessionId)));
        Assert.Equal(1, f.Core.Number("SELECT execution_epoch FROM engine_step_attempt WHERE attempt=2"));
        Assert.Equal(1, f.Core.Number("SELECT authority_epoch FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT attempt_in_generation FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT reminder_sent FROM engine_step"));
        Assert.Equal(0, f.Core.Number("SELECT report_requested FROM engine_step"));
        Assert.Equal(1, f.Core.Number("SELECT held_epoch FROM engine_message WHERE id=$id", new SqlArg("$id", resumed.MessageId)));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start' AND state='pending'"));
        Assert.Equal(1, f.CommittedShells);
        Assert.Equal(archived, f.Core.Text("SELECT json_object('state',state,'receipt',receipt_id,'epoch',execution_epoch,'prompt',prompt_message_id) FROM engine_step_attempt WHERE attempt=1"));
        Assert.Equal("stale_epoch", f.Core.Apply(new Assembly.Engine.Sessions.SetSessionState("idle", "native", null),
            resumed.SessionId!, 3, 0, new(ActorKind.System, "engine.feed")).Error);
    }

    [Fact]
    public void Refused_resumed_prompt_rolls_back_worker_epoch_attempt_effect_and_publications()
    {
        using var f = new Fixture(messageLimit: 1); var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied);
        PrepareReleasedRedo(f, first.SessionId!); var topics = f.Topics.Count;
        var refused = f.Apply(f.Prepare(2000)); Assert.False(refused.Result.Applied); Assert.Equal("message_limit", refused.Result.Error);
        Assert.Equal("ended", f.Core.State("engine_session", first.SessionId!));
        Assert.Equal(0, f.Core.Number("SELECT epoch FROM engine_session WHERE id=$id", new SqlArg("$id", first.SessionId)));
        Assert.Equal("native", f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id=$id", new SqlArg("$id", first.SessionId)));
        Assert.Equal("pending", f.Core.State("engine_step", f.Step)); Assert.Equal(1, f.Core.Count("engine_step_attempt"));
        Assert.Equal(1, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE state='pending'"));
        Assert.Equal(topics, f.Topics.Count); Assert.Equal(1, f.CommittedShells);
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='dispatch_intent' AND generation=1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_workflow_prompt_cannot_bind_resumed_worker_epoch(bool delivered)
    {
        using var f = new Fixture(); var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied);
        Assembly.Engine.Effects.Effect? oldEffect = null;
        if (delivered)
        {
            Assert.True(f.Core.Apply(new Assembly.Engine.Sessions.SetSessionState("idle", "native", null), first.SessionId!, 1, 0, new(ActorKind.System, "engine.feed")).Applied);
            var effectId = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='send'")!;
            oldEffect = Assembly.Engine.Effects.EffectTransitions.Read(f.Core.Db, effectId, default)!;
            using (var snapshot = f.Core.Db.OpenSnapshot(default))
                Assert.NotNull(Assembly.Engine.Messages.MessageDelivery.Read(snapshot, oldEffect));
            f.Core.Db.Writer.Write("test.original-prompt-completed", s => s.Execute("UPDATE engine_message SET state='delivered_to_turn',in_flight=0 WHERE id=$id", new SqlArg("$id", first.MessageId)));
        }
        PrepareReleasedRedo(f, first.SessionId!);
        var resumed = f.Apply(f.Prepare(2000)); Assert.True(resumed.Result.Applied, resumed.Result.Error);
        if (oldEffect is not null)
        {
            using var snapshot = f.Core.Db.OpenSnapshot(default);
            Assert.Null(Assembly.Engine.Messages.MessageDelivery.Read(snapshot, oldEffect, allowDone: true));
        }
        else
        {
            Assert.Equal("failed", f.Core.State("engine_message", first.MessageId!));
            Assert.Equal("moved", f.Core.Text("SELECT reason FROM engine_message WHERE id=$id", new SqlArg("$id", first.MessageId)));
        }
        Assert.Equal(0, f.Core.Number("SELECT target_epoch FROM engine_message WHERE id=$id", new SqlArg("$id", first.MessageId)));
        Assert.Equal(1, f.Core.Number("SELECT target_epoch FROM engine_message WHERE id=$id", new SqlArg("$id", resumed.MessageId)));
    }

    [Theory]
    [InlineData("missing_marker")]
    [InlineData("native_missing")]
    [InlineData("wrong_release_epoch")]
    [InlineData("unsettled_effect")]
    public void Retained_ended_worker_without_exact_resume_proof_stays_blocked(string missing)
    {
        using var f = new Fixture(); var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied);
        PrepareReleasedRedo(f, first.SessionId!, missing);
        using var scope = f.Core.Db.OpenSnapshot(default);
        var admission = f.Commands.ReadWorkflowAdmission(scope, f.Step)!;
        Assert.False(admission.Ready); Assert.Equal("workflow_resume_unavailable", admission.Reason);
        Assert.Null(f.Commands.PrepareWorkflowDispatch(scope, f.Step, 2000, new string('a', 40), new string('b', 64)));
        Assert.Equal(1, f.Core.Count("engine_step_attempt")); Assert.Equal(2, f.Core.Count("engine_session"));
    }

    private static void PrepareReleasedRedo(Fixture f, string session, string? missing = null)
    {
        f.Core.Db.Writer.Write("test.released-redo", s =>
        {
            s.Execute("""
                UPDATE engine_session SET state='ended',provider_session_id=$native,version=version+1 WHERE id=$session;
                INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
                  VALUES('receipt',1,$machine,1000,1000,$run,$step,1,0,$session,0,'hash','build','{}','accepted');
                UPDATE engine_step_attempt SET state='succeeded',ended_ms=1000,receipt_id='receipt' WHERE step_id=$step;
                UPDATE engine_step SET generation=1,attempt_in_generation=0,state='pending',dispatch_state='pending',receipt_id=NULL,reminder_sent=1,report_requested=1 WHERE id=$step;
                UPDATE engine_outbox SET state='done';
                """, new("$native", missing == "native_missing" ? null : "native"), new("$session", session),
                new("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step));
            if (missing != "missing_marker") s.Execute("""
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
                  SELECT 'release',1,$machine,1000,1000,$run,MAX(history_seq)+1,$step,1,0,'workflow_worker_released',1000,
                    json_object('receiptId','receipt','sessionId',$session,'epoch',$epoch,'workspaceId','workspace','head',$head,'providerSessionId','native')
                  FROM engine_workflow_history WHERE run_id=$run
                """, new("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step), new("$session", session),
                new("$epoch", missing == "wrong_release_epoch" ? 99 : 0), new("$head", new string('a', 40)));
            if (missing == "unsettled_effect") s.Execute("UPDATE engine_outbox SET state='uncertain' WHERE kind='start'");
            return 0;
        });
    }

    private sealed class RecordingTopics : ITopics
    {
        public int Count { get; private set; }
        public void Publish(string topic, string? key, string json, bool keyedOnly = false) => Count++;
        public bool HasSubscribers(string topic, string key) => true;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
    private sealed class Fixture : IDisposable
    {
        public RecordingTopics Topics { get; } = new();
        public EngineFixture Core { get; }
        public EngineCommands Commands { get; }
        public string Run { get; } = Guid.NewGuid().ToString("N");
        public string Step { get; }
        public string Prompt { get; }
        public int CommittedShells { get; private set; }
        public Fixture(string prompt = "full snapshotted prompt", int messageLimit = 20, bool failProjection = false, string? yaml = null)
        {
            Prompt = prompt; Core = new(topics: Topics); Core.SeedSession("owner");
            var store = new ProjectStore(Core.Db, Core.Time);
            var project = Core.Db.Writer.Write("test.project", s => store.AddOrRestore(s, Core.Machine, "/test", "Test", null).Project);
            Commands = new(Core.Machine, Core.Limits, () => messageLimit, projectStart: (s, c, command) =>
            {
                s.Publish("test.chat-shell", null, command.Title, EngineJson.Default.String);
                s.AfterCommit(() => CommittedShells++);
                if (failProjection) throw new InvalidOperationException("projection failure");
            }, isConfiguredProvider: (provider, account) => provider == "codex" && account == "");
            var request = new WorkflowStartRequest(Guid.NewGuid(), Run, 0, 0, "owner", "request", ".assembly/workflows/flow.yml", project.Id, "task", null, Json("{}"));
            var context = new CommandContext(request.CommandId, Core.Machine, new(ActorKind.Window, "owner"), Run, 0, 0, 1000);
            var source = WorkflowParser.Parse(new(yaml ?? """
                name: Flow
                steps:
                  build:
                    agent: {provider: codex, model: selected-model}
                    prompt: prompt.md
                    result: build
                """, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("prompt.md", prompt)));
            var start = WorkflowStarts.Prepare(context, request, source, project);
            Assert.True(Core.Db.Writer.Write("test.start", s => Commands.ApplyPreparedWorkflow(s, context, start)).Applied);
            Step = start.Start.StepIds["build"];
            Core.Db.Writer.Write("test.checked-workspace-provider", s => s.Execute("""
                INSERT INTO engine_workflow_workspace(id,version,machine,created_ms,updated_ms,run_id,step_id,allocation_id,path,base_head,head,owned,state)
                VALUES('workspace',1,$machine,1000,1000,$run,$step,'workspace','/test',$head,$head,1,'ready');
                UPDATE engine_step SET workspace_id='workspace' WHERE id=$step;
                UPDATE engine_run SET setup_state='ready',proof_dir='/proof',pr_body='/proof/pr-body.md',work_head=$head WHERE id=$run;
                INSERT INTO engine_provider(id,version,machine,created_ms,updated_ms,provider,account_context,installation,auth_state,facts_json)
                VALUES('codex/',1,$machine,1000,1000,'codex','','installed','signed_in','{"models":[{"id":"selected-model","name":"Selected"}]}');
                """, new SqlArg("$machine", Core.Machine), new SqlArg("$run", Run), new SqlArg("$step", Step), new SqlArg("$head", new string('a', 40))));
        }
        public PreparedWorkflowDispatch Prepare(long nowMs = 1000)
        {
            using var s = Core.Db.OpenSnapshot(default);
            return Commands.PrepareWorkflowDispatch(s, Step, nowMs, new string('a', 40), new string('b', 64))!;
        }
        public WorkflowDispatchAck Apply(PreparedWorkflowDispatch p) => Core.Db.Writer.Write("test.dispatch", s => Commands.ApplyPreparedDispatch(s, p.Context, p));
        public void Dispose() => Core.Dispose();
        private static JsonElement Json(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    }
}
