using System.Collections.Immutable;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Infrastructure.Remote;

// Uses the existing real two-host socket fixture and its isolated owner authentication.
public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Workflow_reads_are_bulk_only_and_use_the_actual_selected_owner_for_all_four_routes()
    {
        await using var h = WorkflowHosts(); await h.Start(); await h.StartMirror();
        var local = SeedWorkflowReads(h.Mac); var remote = SeedWorkflowReads(h.Remote);
        using var control = await Open(h.Mac);
        await Send(control, WorkflowReadCall(1, "runs", new { }));
        Assert.Equal("not_found", (await Read(control)).GetProperty("code").GetString());
        using var bulk = await Open(h.Mac, true); long id = 1;
        foreach (var (machine, seeded) in new[] { ((string?)null, local), (h.Remote.Fixture.Machine, remote) })
        {
            await Send(bulk, WorkflowReadCall(++id, "runs", new { machine }));
            var runs = WorkflowResult(await Read(bulk)).GetProperty("items");
            Assert.Contains(runs.EnumerateArray(), item => item.GetProperty("id").GetString() == seeded.Run);
            Assert.All(runs.EnumerateArray(), item => Assert.Equal(machine ?? h.Mac.Fixture.Machine, item.GetProperty("machine").GetString()));
            await Send(bulk, WorkflowReadCall(++id, "run", new { runId = seeded.Run, machine }));
            var run = WorkflowResult(await Read(bulk));
            Assert.Equal(seeded.Run, run.GetProperty("run").GetProperty("id").GetString());
            Assert.Equal(new[] { "z_first", "a_second" }, run.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("definitionKey").GetString()));
            await Send(bulk, WorkflowReadCall(++id, "history", new { runId = seeded.Run, limit = 1, machine }));
            var history = WorkflowResult(await Read(bulk));
            Assert.Equal(seeded.Run, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("runId").GetString());
            await Send(bulk, WorkflowReadCall(++id, "receipt", new { receiptId = seeded.Receipt, limit = 1, machine }));
            var receipt = WorkflowResult(await Read(bulk));
            Assert.Equal(seeded.Receipt, receipt.GetProperty("receipt").GetProperty("id").GetString());
            Assert.Equal("commits", receipt.GetProperty("sections")[0].GetProperty("section").GetString());
            Assert.True(receipt.TryGetProperty("next", out _));
        }
        await Send(bulk, WorkflowReadCall(++id, "runs", new { machine = "workbox-test" }));
        Assert.Equal("error", (await Read(bulk)).GetProperty("t").GetString());
        await Send(bulk, WorkflowReadCall(++id, "history", new { runId = local.Run, limit = 201 }));
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
        await Send(bulk, WorkflowReadCall(++id, "run", new { runId = local.Run, hidden = true }));
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
        await Send(bulk, WorkflowReadCall(++id, "definitions", new { }));
        Assert.Equal("not_found", (await Read(bulk)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Workflow_bulk_read_refuses_a_changed_owner_connection_before_returning_its_payload()
    {
        await using var h = WorkflowHosts(); await h.Start(); await h.StartMirror();
        var seeded = SeedWorkflowReads(h.Remote);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { entered.TrySetResult(); return release.Task; };
        using var bulk = await Open(h.Mac, true);
        try
        {
            await Send(bulk, WorkflowReadCall(1, "receipt", new { receiptId = seeded.Receipt, machine = h.Remote.Fixture.Machine }));
            await entered.Task.WaitAsync(Wait);
            h.Remote.Endpoint.DropAll();
            release.TrySetResult();
            var result = await Read(bulk);
            Assert.Equal("error", result.GetProperty("t").GetString());
            Assert.Equal("unreachable", result.GetProperty("code").GetString());
            Assert.False(result.TryGetProperty("result", out _));
        }
        finally { release.TrySetResult(); h.Remote.OpeningBulk = null; }
    }

    private static TwoHosts WorkflowHosts()
    {
        var h = new TwoHosts(windowCalls: true, forwardCommands: true);
        h.Mac.AdditionalFeatures = calls => new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(h.Mac.Runtime, h.Mac.Forwarder).Register(calls);
        h.Remote.AdditionalFeatures = calls => new Assembly.Infrastructure.Engine.Workflows.WorkflowCalls(h.Remote.Runtime).Register(calls);
        return h;
    }

    private static (string Run, string Receipt) SeedWorkflowReads(TwoHosts.Host host)
    {
        var f = host.Fixture;
        if (f.Number("SELECT COUNT(*) FROM engine_session WHERE id='s'") == 0) f.SeedSession();
        var projects = new ProjectStore(f.Db, f.Time);
        var project = f.Db.Writer.Write("test.workflow-socket-project", scope => projects.AddOrRestore(scope, f.Machine, "/test", "Read fixture", null).Project);
        var snapshot = WorkflowParser.Parse(new("name: Socket reader\nsteps:\n  z_first: {run: [\"true\"]}\n  a_second: {run: [\"true\"]}\n", ImmutableSortedDictionary<string, string>.Empty));
        var request = new WorkflowStartRequest(Guid.NewGuid(), Guid.NewGuid().ToString("N"), 0, 0, "s", Guid.NewGuid().ToString(), ".assembly/workflows/read.yml", project.Id, "Read fixture", null, JsonSerializer.SerializeToElement(new { }));
        var context = new CommandContext(request.CommandId, f.Machine, new(ActorKind.Window, "owner"), request.ProposedRunId, 0, 0, 1000);
        var prepared = WorkflowStarts.Prepare(context, request, snapshot, project);
        var ack = f.Db.Writer.Write("test.workflow-socket-start", scope => WorkflowStarts.Apply(scope, host.Runtime.Commands, context, prepared));
        Assert.True(ack.Result.Applied, ack.Result.Error);
        f.Db.Writer.Write("test.workflow-socket-history", scope => scope.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,1000,1000,$run,COALESCE(MAX(history_seq),0)+1,0,'fixture_record',1000,'{}'
            FROM engine_workflow_history WHERE run_id=$run
            """, new SqlArg("$id", ack.RunId + "/history"), new("$machine", f.Machine), new("$run", ack.RunId)));
        var receipt = Guid.NewGuid().ToString("N");
        f.Db.Writer.Write("test.workflow-socket-receipt", scope => scope.Execute("""
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,epoch,hash,kind,body_json,state)
            SELECT $id,1,$machine,1000,1000,$run,id,1,1,0,$hash,'build',
              '{"report":{"receipt":{"summary":"Recorded fixture only","commits":[],"tests_added":[]}}}','pending'
            FROM engine_step WHERE run_id=$run ORDER BY id LIMIT 1
            """, new SqlArg("$id", receipt), new("$machine", f.Machine), new("$run", ack.RunId), new("$hash", new string('a', 64))));
        return (ack.RunId, receipt);
    }

    private static string WorkflowReadCall(long id, string method, object args) => JsonSerializer.Serialize(new { t = "call", id, feature = "workflow", method, args });
    private static JsonElement WorkflowResult(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result");
    }
}
