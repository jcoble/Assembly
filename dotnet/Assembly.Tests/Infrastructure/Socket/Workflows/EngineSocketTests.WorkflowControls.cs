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
