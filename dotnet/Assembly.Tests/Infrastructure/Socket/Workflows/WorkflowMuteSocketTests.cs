using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Workflows.Runs;
namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Workflow_mute_read_command_and_stale_refusal_have_remote_parity()
    {
        await using var h = WorkflowHosts(); await h.Start(); await h.StartMirror();
        using var socket = await Open(h.Mac);
        long id = 0;
        foreach (var machine in new[] { (string?)null, h.Remote.Fixture.Machine })
        {
            await Send(socket, WorkflowReadCall(++id, "settings", new { machine }));
            var before = WorkflowResult(await Read(socket));
            Assert.Equal("workflow-settings", before.GetProperty("id").GetString()); Assert.Equal(0, before.GetProperty("version").GetInt64());
            await Send(socket, Command(++id, machine, Guid.NewGuid(), new SetWorkflowMute(10000), entity: "workflow-settings"));
            Assert.True(WorkflowResult(await Read(socket)).Deserialize(EngineJson.Default.CommandResult)!.Applied);
            await Send(socket, WorkflowReadCall(++id, "settings", new { machine }));
            var after = WorkflowResult(await Read(socket)); Assert.Equal(10000, after.GetProperty("mutedUntil").GetInt64()); Assert.Equal(1, after.GetProperty("version").GetInt64());
            using (var bulk = await Open(h.Mac, true))
            {
                await Send(bulk, JsonSerializer.Serialize(new { t = "call", id = ++id, feature = "engine", method = "entities", args = new { refs = new[] { new { kind = "workflow_settings", id = "workflow-settings" } }, machine } }));
                var entity = Assert.Single(WorkflowResult(await Read(bulk)).GetProperty("items").EnumerateArray());
                Assert.Equal(1, entity.GetProperty("version").GetInt64()); Assert.Equal(10000, entity.GetProperty("mutedUntil").GetInt64());
            }
            await Send(socket, Command(++id, machine, Guid.NewGuid(), new SetWorkflowMute(null), entity: "workflow-settings"));
            Assert.Equal("version_conflict", WorkflowResult(await Read(socket)).Deserialize(EngineJson.Default.CommandResult)!.Error);
        }
        Assert.Single(h.Mac.Fixture.Events(), e => e.Kind == "workflow.settings_changed" && e.Machine == h.Mac.Fixture.Machine);
        Assert.Single(h.Remote.Fixture.Events(), e => e.Kind == "workflow.settings_changed" && e.Machine == h.Remote.Fixture.Machine);
    }
    [Fact]
    public async Task Workflow_rerun_and_retry_create_linked_runs_through_real_remote_owner_door()
    {
        await using var h = WorkflowHosts(); await h.Start(); await h.StartMirror();
        var source = SeedWorkflowReads(h.Remote); var f = h.Remote.Fixture;
        f.Db.Writer.Write("test.terminal-source", s => s.Execute("UPDATE engine_run SET state='failed' WHERE id=$id; UPDATE engine_step SET state='failed' WHERE run_id=$id; UPDATE engine_outbox SET state='done' WHERE run_id=$id AND kind='workflow_setup'", new Assembly.Application.Common.Database.SqlArg("$id", source.Run)));
        using var socket = await Open(h.Mac); long id = 0;
        foreach (var failed in new[] { false, true })
        {
            var run = Guid.NewGuid().ToString("N"); var commandId = Guid.NewGuid();
            Assembly.Engine.Contracts.EngineCommand command = failed ? new RetryFailedWorkflow(source.Run, "retry-wire") : new RerunWorkflow(source.Run, "rerun-wire");
            await Send(socket, Command(++id, f.Machine, commandId, command, entity: run));
            var result = WorkflowResult(await Read(socket)).Deserialize(EngineJson.Default.CommandResult)!; Assert.True(result.Applied, result.Error);
            Assert.Equal(source.Run, f.Text("SELECT rerun_of FROM engine_run WHERE id=$id", new Assembly.Application.Common.Database.SqlArg("$id", run)));
            await Send(socket, Command(++id, f.Machine, commandId, command, entity: run));
            Assert.Equal(result, WorkflowResult(await Read(socket)).Deserialize(EngineJson.Default.CommandResult));
            Assert.Equal("failed", f.State("engine_run", source.Run));
        }
        Assert.Equal(0, h.Mac.Fixture.Count("engine_run")); Assert.Equal(3, f.Count("engine_run"));
    }

}
