using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine.Workflows;

namespace Assembly.Tests.Engine.Workflows.Scheduling;

/// <summary>A revision typed at an approval gate reaches the step that did the work, and the run goes on from there.</summary>
public sealed partial class WorkflowScheduleTests
{
    private const string GatedFlow = """
        name: Gated
        steps:
          build:
            agent: {provider: codex, model: selected-model}
            prompt: prompt.md
            result: build
          approve:
            needs: [build]
            gate: {prompt: Ship it?}
        """;

    private static string Gate(Fixture f) => f.Core.Text("SELECT id FROM engine_step WHERE definition_key='approve'")!;

    private static void Finish(Fixture f, string session, int attempt, int generation)
    {
        f.Core.Db.Writer.Write("test.finish-build", s =>
        {
            s.Execute("""
                UPDATE engine_session SET state='ended',provider_session_id='native',version=version+1 WHERE id=$session;
                INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
                  VALUES($receipt,1,$machine,1000,1000,$run,$step,$attempt,$generation,$session,(SELECT epoch FROM engine_session WHERE id=$session),$receipt,'build','{}','accepted');
                UPDATE engine_step_attempt SET state='succeeded',ended_ms=1000,receipt_id=$receipt WHERE step_id=$step AND attempt=$attempt;
                UPDATE engine_step SET state='succeeded',dispatch_state='settled',receipt_id=$receipt,ended_ms=1000 WHERE id=$step;
                UPDATE engine_outbox SET state='done';
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
                  SELECT $receipt||'/worker',1,$machine,1000,1000,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'workflow_worker_released',1000,
                    json_object('receiptId',$receipt,'sessionId',$session,'epoch',a.execution_epoch,'workspaceId','workspace','head',$head,'providerSessionId','native')
                  FROM engine_workflow_history, (SELECT execution_epoch FROM engine_step_attempt WHERE step_id=$step AND attempt=$attempt) a WHERE run_id=$run;
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
                  SELECT $receipt||'/released',1,a.authority_machine,1000,1000,$run,(SELECT COALESCE(MAX(history_seq),0)+1 FROM engine_workflow_history WHERE run_id=$run),$step,$attempt,$generation,'execution_released',1000,
                    json_object('stepId',$step,'attempt',$attempt,'generation',$generation,'sessionId',$session,'epoch',a.execution_epoch,'authorityMachine',a.authority_machine,
                      'terminalSeq',1,'chatRunId','chat','terminalState','finished')
                  FROM engine_step_attempt a WHERE a.step_id=$step AND a.attempt=$attempt;
                """, new("$session", session), new("$receipt", "receipt-" + attempt), new("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step),
                new("$attempt", attempt), new("$generation", generation), new("$head", new string('a', 40)));
            return 0;
        });
    }

    private static string OpenGate(Fixture f, int expected = 1)
    {
        Assert.Equal(expected, f.Core.Db.Writer.Write("test.open-gate", s => WorkflowGates.OpenReady(s, f.Core.Machine, 1500)));
        return f.Core.Text("SELECT id FROM engine_needs_me WHERE state='open'")!;
    }

    private static CommandResult Answer(Fixture f, string need, string answerJson, string channel = "desktop")
    {
        var version = f.Core.Number("SELECT version FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need));
        using var answer = JsonDocument.Parse(answerJson);
        var prepared = EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, need, version, 0,
            new ResolveNeed("answered", answer.RootElement.Clone()), null, null, default, new CommandOrigin(channel, channel == "phone" ? "device" : null, "board"));
        return f.Core.Db.Writer.Write("test.answer-gate", s => f.Commands.ApplyPreparedOwnerNeed(s, prepared, 2000));
    }

    private static string RevisionOf(string text) => JsonSerializer.Serialize(new { choice = "revise", text });

    [Fact]
    public void Revise_sends_the_typed_text_to_the_step_that_did_the_work_and_the_run_goes_on_from_there()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Assert.True(first.Result.Applied, first.Result.Error);
        Finish(f, first.SessionId!, 1, 0);
        var need = OpenGate(f);
        Assert.Equal("[\"approve\",\"revise\",\"deny\"]", f.Core.Text("SELECT options_json FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal("needs_me", f.Core.Text("SELECT state FROM engine_run"));

        var revised = Answer(f, need, RevisionOf("Use the smaller schema"));
        Assert.True(revised.Applied, revised.Error);
        Assert.Equal("answered", f.Core.Text("SELECT state FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal("Use the smaller schema", f.Core.Text("SELECT json_extract(answer_json,'$') FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_run"));
        Assert.Equal("redo_pending", f.Core.Text("SELECT dispatch_state FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step)));
        Assert.Equal("already_resolved", Answer(f, need, RevisionOf("Again")).Error);

        var host = new WorkflowAdvancementPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time);
        Assert.Equal(1, host.AdvancePending(default));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step)));
        Assert.Equal("pending", f.Core.Text("SELECT state FROM engine_step WHERE id=$id", new SqlArg("$id", Gate(f))));
        Assert.Equal(0, f.Core.Number("SELECT paused FROM engine_run"));

        var again = f.Prepare(3000);
        Assert.Equal("Work\n\nThe owner reviewed this at an approval step and asked for a revision:\nUse the smaller schema", again.Body.Prompt);
        var second = f.Apply(again); Assert.True(second.Result.Applied, second.Result.Error);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(again.Body.Prompt, f.Core.Text("SELECT text FROM engine_message WHERE id=$id", new SqlArg("$id", second.MessageId)));

        Finish(f, second.SessionId!, 2, 1);
        var reopened = OpenGate(f);
        Assert.NotEqual(need, reopened);
        var approved = Answer(f, reopened, "\"approve\""); Assert.True(approved.Applied, approved.Error);
        Assert.Equal("succeeded", f.Core.Text("SELECT state FROM engine_step WHERE id=$id", new SqlArg("$id", Gate(f))));
    }

    [Fact]
    public void A_gate_asks_its_prompt_and_its_row_can_say_when_it_closed()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        var need = OpenGate(f);
        Assert.Equal("Ship it?", f.Core.Text("SELECT text FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal("Ship it?", f.Core.Text("SELECT json_extract(presentation_json,'$.title') FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        var row = new EngineReader(f.Core.Db, f.Core.Machine).Entity("needs_me", need, default)!.Value;
        Assert.Equal(1500, row.GetProperty("createdMs").GetInt64());
        Assert.Equal(1500, row.GetProperty("updatedMs").GetInt64());
    }

    private const string TwoGatesFlow = """
        name: Two gates
        steps:
          build:
            agent: {provider: codex, model: selected-model}
            prompt: prompt.md
            result: build
          first:
            needs: [build]
            gate: {prompt: First?}
          second:
            needs: [build]
            gate: {prompt: Second?}
        """;

    private const string GatesInARowFlow = """
        name: Gates in a row
        steps:
          build:
            agent: {provider: codex, model: selected-model}
            prompt: prompt.md
            result: build
          first:
            needs: [build]
            gate: {prompt: First?}
          second:
            needs: [first]
            gate: {prompt: Second?}
        """;

    private static string NeedOf(Fixture f, string gate) =>
        f.Core.Text("SELECT n.id FROM engine_needs_me n JOIN engine_step st ON st.id=n.step_id WHERE n.state='open' AND st.definition_key=$key", new SqlArg("$key", gate))!;

    private static int Advance(Fixture f) => new WorkflowAdvancementPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time).AdvancePending(default);

    private static string StateOf(Fixture f, string need) => f.Core.Text("SELECT state FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need))!;

    private static void RerunAndAskBothAgain(Fixture f)
    {
        Assert.Equal(1, Advance(f));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE state<>'pending'"));
        var again = f.Apply(f.Prepare(3000)); Finish(f, again.SessionId!, 2, 1);
        OpenGate(f, 2);
        Assert.Equal(2, f.Core.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='open'"));
    }

    [Fact]
    public void A_revision_closes_another_open_approval_and_the_rerun_asks_it_again()
    {
        using var f = new Fixture(prompt: "Work", yaml: TwoGatesFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        OpenGate(f, 2);
        var (revised, other) = (NeedOf(f, "first"), NeedOf(f, "second"));

        var answered = Answer(f, revised, RevisionOf("Smaller")); Assert.True(answered.Applied, answered.Error);
        Assert.Equal("superseded", StateOf(f, other));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_needs_me WHERE id=$id AND (answer_json IS NOT NULL OR answered_ms IS NOT NULL)", new SqlArg("$id", other)));
        Assert.Equal("work_redone", f.Core.Text("""
            SELECT json_extract(h.safe_body_json,'$.reason') FROM engine_workflow_history h JOIN engine_step st ON st.id=h.step_id
            WHERE h.kind='workflow_decision' AND json_extract(h.safe_body_json,'$.decision')='closed' AND st.definition_key='second'
            """));
        Assert.Equal("running", f.Core.Text("SELECT state FROM engine_run"));

        Assert.Equal("already_resolved", Answer(f, other, "\"approve\"").Error);
        Assert.Equal("waiting", f.Core.Text("SELECT state FROM engine_step WHERE definition_key='second'"));

        RerunAndAskBothAgain(f);
        var askedAgain = NeedOf(f, "second");
        Assert.NotEqual(other, askedAgain);
        Assert.Equal("superseded", StateOf(f, other));
        Assert.True(Answer(f, askedAgain, "\"approve\"").Applied);
        Assert.True(Answer(f, NeedOf(f, "first"), "\"approve\"").Applied);
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE state<>'succeeded'"));
    }

    [Fact]
    public void An_approval_answered_before_the_revision_stays_answered_and_is_asked_again_after_the_rerun()
    {
        using var f = new Fixture(prompt: "Work", yaml: TwoGatesFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        OpenGate(f, 2);
        var (revised, other) = (NeedOf(f, "first"), NeedOf(f, "second"));

        Assert.True(Answer(f, other, "\"approve\"").Applied);
        var answered = Answer(f, revised, RevisionOf("Smaller")); Assert.True(answered.Applied, answered.Error);
        Assert.Equal("answered", StateOf(f, other));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_needs_me WHERE state='superseded'"));
        RerunAndAskBothAgain(f);
    }

    [Theory]
    [InlineData("\"approve\"")]
    [InlineData("{\"choice\":\"revise\",\"text\":\"Other words\"}")]
    public async Task Two_windows_answering_at_once_end_with_one_clean_result(string otherAnswer)
    {
        for (var round = 0; round < 5; round++)
        {
            using var f = new Fixture(prompt: "Work", yaml: TwoGatesFlow);
            var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
            OpenGate(f, 2);
            var (revised, other) = (NeedOf(f, "first"), NeedOf(f, "second"));

            using var start = new Barrier(2);
            var revising = Task.Run(() => { start.SignalAndWait(); return Answer(f, revised, RevisionOf("Smaller")); });
            var answering = Task.Run(() => { start.SignalAndWait(); return Answer(f, other, otherAnswer); });
            var answers = await Task.WhenAll(revising, answering);

            // The writer takes them in turn: a revision closes the other, and an approval or another revision before it stands.
            var (revision, otherResult) = (answers[0], answers[1]);
            var bothRevise = otherAnswer.Contains("revise", StringComparison.Ordinal);
            Assert.True(bothRevise ? revision.Applied != otherResult.Applied : revision.Applied, revision.Error);
            if (!revision.Applied) Assert.Equal("already_resolved", revision.Error);
            if (!otherResult.Applied) Assert.Equal("already_resolved", otherResult.Error);
            Assert.Equal(otherResult.Applied ? "answered" : "superseded", StateOf(f, other));
            Assert.Equal(revision.Applied ? "answered" : "superseded", StateOf(f, revised));
            RerunAndAskBothAgain(f);
        }
    }

    [Fact]
    public void A_later_gate_not_reopened_yet_does_not_stall_a_revision()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatesInARowFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        Assert.True(Answer(f, OpenGate(f), "\"approve\"").Applied);
        Assert.True(Answer(f, OpenGate(f), RevisionOf("One")).Applied);
        Assert.Equal(1, Advance(f));
        var second = f.Apply(f.Prepare(3000)); Finish(f, second.SessionId!, 2, 1);

        var answered = Answer(f, OpenGate(f), RevisionOf("Two")); Assert.True(answered.Applied, answered.Error);
        Assert.Equal(1, Advance(f));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_step WHERE state<>'pending'"));
        Assert.Contains("Two", f.Prepare(4000).Body.Prompt);
    }

    /// <summary>The redone step's worker dies; the owner retries it, which starts another generation of that step alone.</summary>
    private static void LoseWorkerAndRetry(Fixture f, string session, int attempt, int generation)
    {
        Finish(f, session, attempt, generation);
        f.Core.Db.Writer.Write("test.lose-worker", s => s.Execute(
            "UPDATE engine_step SET state='waiting',outcome_reason='session_lost',dispatch_state='lost',receipt_id=NULL,ended_ms=NULL WHERE id=$step",
            new SqlArg("$step", f.Step)));
        var retried = f.Core.Apply(new RetryWorkflowStep(f.Step), f.Run, f.Core.Number("SELECT version FROM engine_run"));
        Assert.True(retried.Applied, retried.Error);
    }

    [Theory]
    [InlineData("Work", "Work\n\nThe owner reviewed this at an approval step and asked for a revision:\nSmaller schema")]
    [InlineData("Work ${{redo.findings}}", "Work [\"Smaller schema\"]")]
    public void The_owners_words_survive_a_retry_of_the_redone_step(string prompt, string expected)
    {
        using var f = new Fixture(prompt: prompt, yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        Assert.True(Answer(f, OpenGate(f), RevisionOf("Smaller schema")).Applied);
        Assert.Equal(1, Advance(f));
        var second = f.Apply(f.Prepare(3000)); Assert.True(second.Result.Applied, second.Result.Error);

        LoseWorkerAndRetry(f, second.SessionId!, 2, 1);
        Assert.Equal(2, f.Core.Number("SELECT generation FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step)));
        Assert.Equal(expected, f.Prepare(4000).Body.Prompt);
    }

    [Fact]
    public void A_failed_agent_step_runs_again_once_it_is_retried()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        f.Core.Db.Writer.Write("test.failed", s => s.Execute(
            "UPDATE engine_step SET state='failed',dispatch_state='settled',receipt_id=NULL,max_attempts=3 WHERE id=$step",
            new SqlArg("$step", f.Step)));
        var retried = f.Core.Apply(new RetryWorkflowStep(f.Step), f.Run, f.Core.Number("SELECT version FROM engine_run"));
        Assert.True(retried.Applied, retried.Error);
        Assert.Equal("Work", f.Prepare(2000).Body.Prompt);
    }

    [Fact]
    public void A_later_redo_without_the_owners_words_stops_repeating_them()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        Assert.True(Answer(f, OpenGate(f), RevisionOf("Smaller schema")).Applied);
        Assert.Equal(1, Advance(f));
        f.Core.Db.Writer.Write("test.later-redo", s => s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT 'later',1,$machine,1000,1000,$run,MAX(history_seq)+1,$step,1,1,'redo_applied',1000,json_object('targetStepId',$step,'generation',1,'findingReceiptIds',json_array())
            FROM engine_workflow_history WHERE run_id=$run;
            UPDATE engine_step SET generation=2 WHERE id=$step;
            """, new SqlArg("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step)));
        Assert.Equal("Work", f.Prepare(3000).Body.Prompt);
    }

    [Fact]
    public void An_owners_revision_does_not_use_up_the_reviewers_rounds()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        f.Core.Db.Writer.Write("test.one-round", s => s.Execute("UPDATE engine_run SET max_rounds=1"));
        Assert.True(Answer(f, OpenGate(f), RevisionOf("One more")).Applied);
        Assert.Equal(1, Advance(f));
        Assert.Equal(0, f.Core.Number("SELECT MAX(round) FROM engine_step"));

        // A reviewer's finding now: the one round it has is still there, and it counts.
        f.Core.Db.Writer.Write("test.reviewer-finding", s => s.Execute("""
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
              VALUES('found',1,$machine,1000,1000,$run,$step,1,1,NULL,0,'found','review','{"report":{"receipt":{"findings":[{"text":"A finding"}]}}}','accepted');
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
              SELECT 'asked',1,$machine,1000,1000,$run,MAX(history_seq)+1,$step,1,1,'redo_requested',1000,
                json_object('targetStepId',$step,'generation',1,'sourceStepId',$step,'receiptId','found','peerStepIds',json_array(),'sourceKind','review')
              FROM engine_workflow_history WHERE run_id=$run;
            UPDATE engine_step SET dispatch_state='redo_pending' WHERE id=$step;
            """, new SqlArg("$machine", f.Core.Machine), new("$run", f.Run), new("$step", f.Step)));
        Assert.Equal(1, Advance(f));
        Assert.Equal(0, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(1, f.Core.Number("SELECT round FROM engine_step WHERE id=$id", new SqlArg("$id", f.Step)));
    }

    [Fact]
    public void A_prompt_with_its_own_place_for_findings_is_not_given_the_text_twice()
    {
        using var f = new Fixture(prompt: "Work ${{redo.findings}}", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        Assert.True(Answer(f, OpenGate(f), RevisionOf("Smaller schema")).Applied);
        Assert.Equal(1, new WorkflowAdvancementPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time).AdvancePending(default));
        Assert.Equal("Work [\"Smaller schema\"]", f.Prepare(3000).Body.Prompt);
    }

    [Fact]
    public void The_round_limit_does_not_stop_a_revision_the_owner_asked_for()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        f.Core.Db.Writer.Write("test.at-limit", s => s.Execute("UPDATE engine_run SET max_rounds=1; UPDATE engine_step SET round=1"));
        Assert.True(Answer(f, OpenGate(f), RevisionOf("One more")).Applied);
        Assert.Equal(1, new WorkflowAdvancementPreparation(f.Core.Db, f.Commands, f.Core.Machine, f.Core.Time).AdvancePending(default));
        Assert.Equal(0, f.Core.Number("SELECT paused FROM engine_run"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='redo_stopped'"));
    }

    [Theory]
    [InlineData("\"   \"")]
    [InlineData("\"\"")]
    public void A_revision_with_nothing_typed_leaves_the_gate_open(string text)
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        var need = OpenGate(f);
        var answer = Answer(f, need, "{\"choice\":\"revise\",\"text\":" + text + "}");
        Assert.Equal("reply_required", answer.Error);
        Assert.Equal("open", f.Core.Text("SELECT state FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='gate_revised'"));
    }

    [Fact]
    public void A_phone_cannot_send_a_revision()
    {
        using var f = new Fixture(prompt: "Work", yaml: GatedFlow);
        var first = f.Apply(f.Prepare()); Finish(f, first.SessionId!, 1, 0);
        Assert.Equal("forbidden", Answer(f, OpenGate(f), RevisionOf("From the phone"), "phone").Error);
    }

    [Theory]
    [InlineData("build", "build")]
    [InlineData("check", "build")]
    [InlineData("review", "build")]
    [InlineData("first", null)]
    public void A_revision_goes_to_who_did_the_work(string gateNeeds, string? expected)
    {
        var source = WorkflowParser.Parse(new($$"""
            name: Targets
            steps:
              first:
                gate: {prompt: Start?}
              build:
                needs: [first]
                agent: {provider: codex}
                prompt: prompt.md
                result: build
              check:
                needs: [build]
                run: ["true"]
                on_fail: build
              review:
                needs: [build]
                review: {of: build, agent: {provider: codex}, prompt: prompt.md}
                result: review
              approve:
                needs: [{{gateNeeds}}]
                gate: {prompt: Ship?}
            """, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("prompt.md", "Work")));
        Assert.Equal(expected, WorkflowGateRevision.Nearest(source.Definition.Steps, "approve", _ => true));
    }

    [Fact]
    public void A_skipped_worker_is_passed_over_for_the_one_before_it()
    {
        var source = WorkflowParser.Parse(new("""
            name: Skips
            steps:
              early:
                agent: {provider: codex}
                prompt: prompt.md
                result: build
              late:
                needs: [early]
                agent: {provider: codex}
                prompt: prompt.md
                result: build
              approve:
                needs: [late]
                gate: {prompt: Ship?}
            """, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("prompt.md", "Work")));
        Assert.Equal("early", WorkflowGateRevision.Nearest(source.Definition.Steps, "approve", key => key != "late"));
    }
}
