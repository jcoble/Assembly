using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Execution;
using Assembly.Engine.Persistence;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Reviews;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Scheduling;
using Assembly.Infrastructure.Engine.Workflows;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Workflows.Execution;

public sealed partial class WorkflowAdvancementTests
{
    [Fact]
    public async Task Existing_worker_callback_coalesces_once_only_after_exact_original_releases()
    {
        var topics = new Topics(); using var f = new Fixture(topics);
        f.Core.Db.Writer.Write("test.pending-peer", s => s.Execute("UPDATE engine_step SET state='pending',dispatch_state=NULL WHERE id='review2'"));
        f.Finding("review1", "first finding");
        var journals = f.Core.Count("engine_command_receipt");
        Assert.Equal(0, f.Host.AdvancePending(default));
        Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        f.Core.Db.Writer.Write("test.started-peer", s => s.Execute("UPDATE engine_step SET state='running',dispatch_state='running' WHERE id='review2'"));
        f.Finding("review2", "second finding");
        foreach (var step in new[] { "build", "review1", "review2" }) f.Terminal(step);
        journals = f.Core.Count("engine_command_receipt");
        Assert.Equal(0, f.Host.AdvancePending(default)); // ended rows plus terminal chat rows are not release.
        Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        f.Release("build"); f.Release("review1");
        Assert.Equal(0, f.Host.AdvancePending(default));
        f.Release("review2");
        var original = new CommandContext(DispatchIdentity.Id("workflow-generation-advance/v1", f.Core.Machine, "r", "build", "0",
            f.Core.Number("SELECT version FROM engine_step WHERE id='build'").ToString(System.Globalization.CultureInfo.InvariantCulture)),
            f.Core.Machine, new(ActorKind.System, "engine.worker"), "build", f.Core.Number("SELECT version FROM engine_step WHERE id='build'"), 7, 1000);
        using var passes = new Passes(topics); using var stop = new CancellationTokenSource();
        var calls = 0; var worker = new EngineWorker(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time, null, passes, ct =>
        {
            calls++; f.Host.AdvancePending(ct);
            return Task.FromResult<IReadOnlyList<PreparedWorkflowDispatch>>([]);
        });
        var running = worker.RunAsync(stop.Token);
        try
        {
            await passes.Next(); Assert.Equal(1, calls);
            Assert.Equal(4, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE generation=1"));
            Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_applied'"));
            Assert.Equal(2, f.Core.Number("SELECT json_array_length(json_extract(safe_body_json,'$.findingReceiptIds')) FROM engine_workflow_history WHERE kind='redo_applied'"));
            Assert.Equal("build-worker", f.Core.Text("SELECT session_id FROM engine_step WHERE id='build'"));
            Assert.Equal("native-original", f.Core.Text("SELECT provider_session_id FROM engine_session WHERE id='build-worker'"));
            Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id='build' AND attempt=1"));
            Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE kind='review' AND session_id IS NOT NULL"));
            var events = f.Core.Count("engine_event"); journals = f.Core.Count("engine_command_receipt");
            var replay = f.Core.Db.Writer.Write("test.advance-replay", s => f.Commands.AdvanceWorkflowGeneration(s, original with { NowMs = 5000 }));
            Assert.True(replay.Applied, replay.Error);
            Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
            worker.Wake(); await passes.Next(); Assert.Equal(2, calls);
            Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_applied'"));
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Actual_command_redo_waits_for_original_outbox_drain_and_advances_once()
    {
        using var f = new CommandStepTests.Fixture(["exit 7", "printf collected"], "allow", onFail: true);
        await f.ReadyBuildAsync();
        Assert.True(f.Start(f.Prepare()).Applied); var first = f.Claim();
        Assert.True(f.Complete(first, await f.Host().ExecuteAsync(first, default)).Applied);
        Assert.True(f.Start(f.Prepare()).Applied); var last = f.Claim();
        Assert.True(f.Complete(last, await f.Host().ExecuteAsync(last, default)).Applied);
        var host = new WorkflowAdvancementPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time);
        f.Core.Db.Writer.Write("test.unsettled-outbox", s => s.Execute("UPDATE engine_outbox SET state='unavailable' WHERE id=$id", new SqlArg("$id", last.Id)));
        var journals = f.Core.Count("engine_command_receipt");
        Assert.Equal(0, host.AdvancePending(default));
        Assert.Equal(journals, f.Core.Count("engine_command_receipt"));
        Assert.Equal(0, f.Core.Number("SELECT generation FROM engine_step WHERE definition_key='build'"));
        f.Core.Db.Writer.Write("test.settled-outbox", s => s.Execute("UPDATE engine_outbox SET state='done' WHERE id=$id", new SqlArg("$id", last.Id)));
        Assert.Equal(1, host.AdvancePending(default));
        Assert.Equal(1, f.Core.Number("SELECT generation FROM engine_step WHERE definition_key='build'"));
        Assert.Equal(0, host.AdvancePending(default));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_applied'"));
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step_attempt WHERE step_id=(SELECT id FROM engine_step WHERE definition_key='build') AND attempt=1"));
    }

    private sealed class Fixture : IDisposable
    {
        internal EngineFixture Core { get; }
        internal EngineCommands Commands => new(Core.Machine, Core.Limits);
        internal WorkflowAdvancementPreparation Host => new(Core.Db, Commands, Core.Machine, Core.Time);
        internal Fixture(Topics topics)
        {
            Core = new(topics: topics); Core.SeedSession("owner"); Core.SeedRun("r", "owner", "running");
            foreach (var step in new[] { "build", "review1", "review2", "tail" })
            {
                var started = step != "tail";
                if (started) Core.SeedSession(step + "-worker", "ended", "owner", "r", epoch: 7);
                Core.SeedStep(step, "r", started ? "running" : "pending", started ? step + "-worker" : null, started ? 1 : 0);
            }
            var yaml = "name: Redo\nsteps:\n  build:\n    agent: { provider: codex }\n    prompt: prompt.md\n    result: build\n  review1:\n    needs: [build]\n    review: { of: build, agent: { provider: codex }, prompt: prompt.md }\n    result: review\n  review2:\n    needs: [build]\n    review: { of: build, agent: { provider: codex }, prompt: prompt.md }\n    result: review\n  tail:\n    needs: [review1,review2]\n    run: [printf later]\n";
            var snapshot = WorkflowParser.Parse(new(yaml, ImmutableSortedDictionary<string, string>.Empty.Add("prompt.md", "Work")));
            var request = new WorkflowStartRequest(Guid.NewGuid(), "r", 0, 0, "owner", "redo", "workflow.yml", "project", "Redo", null, JsonSerializer.SerializeToElement(new { }));
            var body = new WorkflowStartBody(new(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "fixture"), "r", 0, 0, 1000), request,
                "project", "/tmp/advance-fixture", snapshot, new(request.Inputs, "inputs"),
                ImmutableSortedDictionary<string, string>.Empty.Add("build", "build").Add("review1", "review1").Add("review2", "review2").Add("tail", "tail"), "request");
            Core.Db.Writer.Write("test.redo-definition", s =>
            {
                s.Execute("""
                    UPDATE engine_run SET max_rounds=5,definition_hash=$hash,setup_state='ready' WHERE id='r';
                    INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
                    VALUES('snapshot',1,$machine,1000,1000,'r',1,$hash,'inputs',$body);
                    INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES('review1','build',0),('review2','build',0),('tail','review1',0),('tail','review2',0);
                    UPDATE engine_session SET provider_session_id='native-original' WHERE id='build-worker';
                    """, new("$machine", Core.Machine), new("$hash", snapshot.Hash), new("$body", JsonSerializer.Serialize(body, WorkflowPreparationJson.Default.WorkflowStartBody)));
                foreach (var step in new[] { "build", "review1", "review2" })
                    s.Execute("""
                        UPDATE engine_step SET definition_key=$step,kind=$kind,result=$result,authority_machine=$machine,authority_session_id=$session,authority_epoch=7,
                          dispatch_state='running' WHERE id=$step;
                        INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,execution_epoch,session_id,authority_machine,gate_state)
                        VALUES($step,1,'running',1000,0,0,7,$session,$machine,'executing');
                        """, new("$step", step), new("$kind", step == "build" ? "agent" : "review"), new("$result", step == "build" ? "build" : "review"),
                        new("$machine", Core.Machine), new("$session", step + "-worker"));
                return s.Execute("""
                    UPDATE engine_step SET definition_key='tail',kind='command' WHERE id='tail';
                    UPDATE engine_step SET state='succeeded',ended_ms=1000 WHERE id='build';
                    UPDATE engine_step_attempt SET state='succeeded',ended_ms=1000,gate_state='settled' WHERE step_id='build' AND attempt=1;
                    """);
            });
        }
        internal void Finding(string step, string text)
        {
            var attempt = checked((int)Core.Number("SELECT attempt FROM engine_step WHERE id=$step", new SqlArg("$step", step)));
            var generation = checked((int)Core.Number("SELECT generation FROM engine_step WHERE id=$step", new SqlArg("$step", step)));
            var session = Core.Text("SELECT session_id FROM engine_step WHERE id=$step", new SqlArg("$step", step))!;
            var epoch = Core.Number("SELECT authority_epoch FROM engine_step WHERE id=$step", new SqlArg("$step", step));
            var receiptId = step + "-receipt-" + generation;
            var report = JsonSerializer.Serialize(new { report = new { receipt = new { verdict = "fail", findings = new[] { new { severity = "blocking", late = false, path_line = "src/work.cs:1", text, fix = "Apply the correction", grades_of_earlier = Array.Empty<string>() } }, head = new string('a', 40) } } });
            Core.Db.Writer.Write("test.accepted-review", s =>
            {
                s.Execute("""
                    INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
                    VALUES($id,1,$machine,1000,1000,'r',$step,$attempt,$generation,$session,$epoch,$id,'review',$body,'accepted');
                    """, new("$id", receiptId), new("$machine", Core.Machine), new("$step", step), new("$session", session), new("$body", report), new("$attempt", attempt), new("$generation", generation), new("$epoch", epoch));
                var c = new CommandContext(Guid.NewGuid(), Core.Machine, new(ActorKind.System, "receipt.check"), step,
                    s.QueryFirst("SELECT version FROM engine_step WHERE id=$step", r => r.Long(0), new SqlArg("$step", step)), epoch, 1000);
                var result = ReviewTransitions.Finished(s, c, receiptId); Assert.True(result.Applied, result.Error); return result;
            });
        }
        internal void Terminal(string step)
        {
            var session = Core.Text("SELECT session_id FROM engine_step WHERE id=$step", new SqlArg("$step", step))!;
            var chat = step + "-turn-" + Core.Number("SELECT generation FROM engine_step WHERE id=$step", new SqlArg("$step", step));
            var rows = new RowBatch(); rows.Rows.Add(new(chat, null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 1000));
            rows.Rows.Add(new(chat, null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 1001));
            RowStore.Open(Core.Db).Commit(session, rows);
        }
        internal void Release(string step)
        {
            PreparedWorkflowRelease prepared;
            using (var s = Core.Db.OpenSnapshot(default))
            {
                var current = s.QueryFirst("SELECT session_id,attempt,generation,authority_epoch FROM engine_step WHERE id=$step",
                    r => (Session: r.Text(0), Attempt: (int)r.Long(1), Generation: r.Long(2), Epoch: r.Long(3)), new SqlArg("$step", step));
                prepared = EngineCommands.PrepareWorkflowRelease(s, new(current.Session, "r", step, current.Attempt, current.Epoch),
                    RowStore.TerminalReply(s, current.Session, step + "-turn-" + current.Generation)!)!;
            }
            var result = Core.Db.Writer.Write("test.real-release-command", s => Commands.ApplyWorkflowRelease(s, prepared, 1002));
            Assert.True(result.Applied, result.Error);
        }
        internal void NextRound()
        {
            var generation = Core.Number("SELECT generation FROM engine_step WHERE id='build'");
            foreach (var review in new[] { "review1", "review2" }) Core.SeedSession(review + "-worker-" + generation, "ended", "owner", "r", epoch: 7 + generation);
            Core.Db.Writer.Write("test.next-completed-build-and-reviewers", s =>
            {
                foreach (var step in new[] { "build", "review1", "review2" })
                {
                    var session = step == "build" ? "build-worker" : step + "-worker-" + generation;
                    s.Execute("""
                        UPDATE engine_session SET epoch=$epoch WHERE id=$session;
                        UPDATE engine_step SET attempt=attempt+1,state=$state,session_id=$session,authority_session_id=$session,authority_epoch=$epoch,
                          dispatch_state='running' WHERE id=$step;
                        INSERT INTO engine_step_attempt(step_id,attempt,state,started_ms,late,generation,execution_epoch,session_id,authority_machine,gate_state)
                        SELECT id,attempt,$state,1000,0,generation,$epoch,$session,$machine,'executing' FROM engine_step WHERE id=$step;
                        """, new("$epoch", 7 + generation), new("$session", session), new("$step", step), new("$state", step == "build" ? "succeeded" : "running"), new("$machine", Core.Machine));
                }
                return 0;
            });
        }
        public void Dispose() => Core.Dispose();
    }

    private sealed class Passes : IEngineRetention, IDisposable
    {
        private readonly Channel<bool> _passes = Channel.CreateUnbounded<bool>();
        private readonly IDisposable _subscription;
        internal Passes(Topics topics) => _subscription = topics.Subscribe("test.advance.pass", null, _ => { }, (_, _) => _passes.Writer.TryWrite(true));
        public void Apply(IWriteScope s, long now) => s.Publish("test.advance.pass", null, now, EngineJson.Default.Int64);
        public long? NextDeadlineMs(IReadScope s, long now) => null;
        internal async Task Next() => await _passes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Dispose() => _subscription.Dispose();
    }
}
