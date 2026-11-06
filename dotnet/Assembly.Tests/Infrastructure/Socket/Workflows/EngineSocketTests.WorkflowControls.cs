using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Every_window_control_routes_and_returns_CommandResult()
    {
        _f.SeedSession("owner"); _f.SeedRun(ownerSessionId: "owner", state: "running"); _f.SeedStep();
        _f.Db.Writer.Write("test.workflow", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'"));
        using var socket = await Open();
        EngineCommand[] commands = [new PauseWorkflow(),new ResumeWorkflow(),new RetryWorkflowStep("step"),new DecideWorkflowStep("step","retry"),
            new RerunWorkflow("r","key"),new RetryFailedWorkflow("r","key"),new KeepWorkflowSucceeded(),new UndoWorkflowKeep(Guid.NewGuid().ToString()),
            new GrantWorkflowScope("step","repo"),new SetWorkflowMute(10000)];
        foreach (var command in commands)
        {
            var id = command is RerunWorkflow ? Guid.NewGuid().ToString("N") : command is SetWorkflowMute ? "workflow-settings" : "r";
            var version = id == "r" ? _f.Number("SELECT version FROM engine_run WHERE id='r'") : 0;
            var result = await Command(socket, command, id, version);
            Assert.DoesNotContain(result.Error, new[] { "forbidden", "unsupported_command" });
            Assert.False(result.Late);
        }
        Assert.Equal(10000, _f.Number("SELECT muted_until FROM engine_workflow_settings"));
    }
    [Fact]
    public async Task A_window_retries_a_stuck_step_through_the_socket()
    {
        _f.SeedSession("owner"); _f.SeedRun(ownerSessionId: "owner", state: "running"); _f.SeedStep(state: "waiting", attempt: 1);
        _f.Db.Writer.Write("test.stuck", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'; UPDATE engine_step SET kind='agent',outcome_reason='session_lost',attempt_in_generation=1"));
        using var socket = await Open();
        var result = await Command(socket, new RetryWorkflowStep("step"), "r");
        Assert.True(result.Applied, result.Error);
        Assert.Equal("pending", _f.State("engine_step", "step"));
        Assert.Equal(1, _f.Number("SELECT generation FROM engine_step"));
    }
    private void SeedGateWithWorkBefore()
    {
        _f.SeedSession("owner"); _f.SeedRun(ownerSessionId: "owner", state: "running");
        _f.SeedStep("work", state: "succeeded"); _f.SeedStep("gate", maxAttempts: 2);
        _f.Db.Writer.Write("test.gate", s => s.Execute("""
            UPDATE engine_run SET definition_hash='workflow',setup_state='ready';
            UPDATE engine_step SET kind='agent',definition_key='work' WHERE id='work';
            UPDATE engine_step SET kind='gate',definition_key='gate' WHERE id='gate';
            INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES('gate','work',0);
            INSERT INTO engine_workflow_snapshot(id,version,machine,created_ms,updated_ms,run_id,schema_version,definition_hash,input_hash,snapshot_json)
            VALUES('r',1,$machine,1000,1000,'r',1,'workflow','inputs','{"snapshot":{"definition":{"steps":{"work":{"id":"work","kind":"agent","needs":[]},"gate":{"id":"gate","kind":"gate","gate":"Approve","needs":["work"]}}}}}');
            """, new Assembly.Application.Common.Database.SqlArg("$machine", _f.Machine)));
        Assert.Equal(1, _f.Db.Writer.Write("test.open-gate", s => WorkflowGates.OpenReady(s, _f.Machine, 1000)));
    }
    [Fact]
    public async Task A_window_revises_a_gate_through_resolve_need_and_the_text_goes_to_the_work_before_it()
    {
        SeedGateWithWorkBefore();
        Assert.Equal("[\"approve\",\"revise\",\"deny\"]", _f.Text("SELECT options_json FROM engine_needs_me"));
        Assert.Equal("choice", _f.Text("SELECT json_extract(presentation_json,'$.inputMode') FROM engine_needs_me"));
        Assert.Equal("revise", _f.Text("SELECT json_extract(presentation_json,'$.options[1].id') FROM engine_needs_me"));
        var need = _f.Text("SELECT id FROM engine_needs_me")!;
        using var socket = await Open();
        var answer = JsonSerializer.SerializeToElement(new { choice = "revise", text = "Smaller schema" });
        var result = await Command(socket, new Assembly.Engine.NeedsMe.ResolveNeed("answered", answer), need, 1);
        Assert.True(result.Applied, result.Error);
        Assert.Equal("Smaller schema", _f.Text("SELECT json_extract(safe_body_json,'$.text') FROM engine_workflow_history WHERE kind='gate_revised'"));
        Assert.Equal("work", _f.Text("SELECT json_extract(safe_body_json,'$.targetStepId') FROM engine_workflow_history WHERE kind='gate_revised'"));
        Assert.Equal("redo_pending", _f.Text("SELECT dispatch_state FROM engine_step WHERE id='work'"));
    }
    [Fact]
    public async Task A_phone_revises_a_gate_through_resolve_need()
    {
        SeedGateWithWorkBefore();
        _overrideTitlePeer = true; _titlePeer = new PhonePeer("controls-phone");
        using var socket = await Open();
        var answer = JsonSerializer.SerializeToElement(new { choice = "revise", text = "From the phone" });
        var result = await Command(socket, new Assembly.Engine.NeedsMe.ResolveNeed("answered", answer), _f.Text("SELECT id FROM engine_needs_me")!, 1);
        Assert.True(result.Applied, result.Error);
        Assert.Equal("From the phone", _f.Text("SELECT json_extract(safe_body_json,'$.text') FROM engine_workflow_history WHERE kind='gate_revised'"));
    }
    [Fact]
    public async Task Phone_cannot_submit_system_checks_or_gate_decisions_but_can_pause_resume_and_mute()
    {
        _f.SeedSession("owner"); _f.SeedRun(ownerSessionId: "owner", state: "running"); _f.SeedStep();
        _f.Db.Writer.Write("test.workflow", s => s.Execute("UPDATE engine_run SET definition_hash='workflow'"));
        _overrideTitlePeer = true; _titlePeer = new PhonePeer("controls-phone");
        using var socket = await Open();
        Assert.True((await Command(socket, new PauseWorkflow(), "r")).Applied);
        Assert.True((await Command(socket, new ResumeWorkflow(), "r", 1)).Applied);
        Assert.True((await Command(socket, new SetWorkflowMute(10000), "workflow-settings")).Applied);
        foreach (var command in new EngineCommand[] { new GrantWorkflowScope("step", "repo"), new DecideWorkflowStep("step", "approve"), new SetSessionState("ended", null, null) })
            Assert.Equal("forbidden", (await Command(socket, command, "r", 2)).Error);
        Assert.Equal(0, _f.Number("SELECT scope_granted FROM engine_step"));
    }
}
