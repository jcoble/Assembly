using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
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
                  VALUES($receipt,1,$machine,1000,1000,$run,$step,$attempt,$generation,$session,0,$receipt,'build','{}','accepted');
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
