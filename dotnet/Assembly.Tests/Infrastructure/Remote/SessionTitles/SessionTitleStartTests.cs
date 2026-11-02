using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote.SessionTitles;

public sealed class SessionTitleStartTests
{
    [Fact]
    public async Task Real_Mcp_bridge_with_full_oversized_caller_title_commits_target_child_and_exact_source_initial_message()
    {
        var root = Path.Combine(Path.GetTempPath(), "assembly-title-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mac")); Directory.CreateDirectory(Path.Combine(root, "remote"));
        try
        {
            var launcher = new RelayTestLauncher();
            await using var h = new TwoHosts(forwardCommands: true, machineLauncher: launcher, checkouts: root);
            await h.Start();
            var parent = Guid.NewGuid().ToString();
            var title = new string('界', 1500000) + "\0FULL_CALLER😀";
            var start = EngineCommands.PrepareSessionTitle(new StartSession("codex", "model", Path.Combine(root, "mac"), title, null, null, null));
            var result = ((IEngineRuntime)h.Mac.Runtime).Write(scope => h.Mac.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Window, "owner"), parent, 0, 0, 1000), start));
            Assert.True(result.Applied, result.Error);
            await h.StartMirror(); await Ready(h);
            var forward = new MachineForward(machine => h.Connections.ByEngineMachine(h.Mac.Fixture.Db, machine));
            var starts = new MachineStarts(h.Mac.Runtime, forward, h.Connections, h.Mac.Fixture.Limits, h.Mac.Fixture.Time);
            var answer = await new McpMachineStarts(starts).StartAsync(new(parent, 0),
                new StartSessionArguments(Prompt: "TASK\0FULL_PROMPT😀", Title: "Child", Folder: Path.Combine(root, "remote"), Machine: h.Remote.Runtime.Machine), default);
            Assert.Equal("starting", h.Remote.Fixture.State("engine_session", answer.SessionId));
            Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM session_info WHERE session_id=$id", new SqlArg("$id", answer.SessionId)));
            Assert.Equal(title, h.Mac.Sessions.Get(parent)!.Title);
            var message = h.Mac.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB),CAST(sender_label AS BLOB),from_session_id,to_session_id,state FROM engine_message WHERE id=$id",
                row => (Body: Encoding.UTF8.GetString(row.Blob(0)), Label: Encoding.UTF8.GetString(row.Blob(1)), From: row.Text(2), To: row.Text(3), State: row.Text(4)),
                default, new SqlArg("$id", answer.MessageId));
            var expected = $"Session \"{title}\" ({parent}) started you. Do the task below. Your last reply each turn goes back to it as a message; you can also message it with the Assembly MCP server's send_message tool.\n\nTASK\0FULL_PROMPT😀";
            Assert.Equal(expected, message.Body);
            Assert.Equal($"{title} ({parent})", message.Label);
            Assert.Equal(parent, message.From); Assert.Equal(answer.SessionId, message.To); Assert.Equal("queued", message.State);
            Assert.Equal(1, h.Mac.Fixture.Count("engine_message")); Assert.Equal(0, h.Remote.Fixture.Count("engine_message"));
            Assert.Equal(0, launcher.ProviderCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_coordinator_preserves_huge_new_child_title_and_resolves_bulk_only_lost_commit_reply(bool loseReply)
    {
        var root = Path.Combine(Path.GetTempPath(), "assembly-title-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mac")); Directory.CreateDirectory(Path.Combine(root, "remote"));
        try
        {
            var launcher = new RelayTestLauncher();
            await using var h = new TwoHosts(forwardCommands: true, machineLauncher: launcher, checkouts: root);
            await h.Start();
            var parent = Guid.NewGuid().ToString();
            var preparedParent = EngineCommands.PrepareSessionTitle(new StartSession("codex", null, Path.Combine(root, "mac"), "Parent", null, null, null));
            Assert.True(((IEngineRuntime)h.Mac.Runtime).Write(scope => h.Mac.Runtime.Commands.ApplyPreparedSessionTitle(scope,
                new(Guid.NewGuid(), h.Mac.Runtime.Machine, new(ActorKind.Window, "owner"), parent, 0, 0, 1000), preparedParent)).Applied);
            await h.StartMirror(); await Ready(h);
            var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
            var lost = 0;
            if (loseReply) h.Remote.WriteCommitted = what =>
            {
                if (what == "engine/start_title_commit" && Interlocked.Increment(ref lost) == 1) h.Remote.BulkEndpoint.DropAll();
            };
            var forward = new MachineForward(machine => h.Connections.ByEngineMachine(h.Mac.Fixture.Db, machine));
            var starts = new MachineStarts(h.Mac.Runtime, forward, h.Connections, h.Mac.Fixture.Limits, h.Mac.Fixture.Time);
            var child = Guid.NewGuid().ToString(); var commandId = Guid.NewGuid();
            var title = new string('界', 1500000) + "\0FULL_NEW_CHILD😀";
            var command = new StartSession("codex", null, Path.Combine(root, "remote"), title, parent, null, null, ManagedCheckout: false);
            var result = await starts.StartAsync(new(commandId, h.Mac.Runtime.Machine, new(ActorKind.Window, "owner"), child, 0, 0, 1000),
                command, h.Remote.Runtime.Machine, default);
            Assert.True(result.Applied, result.Error);
            Assert.Equal(loseReply ? 1 : 0, lost);
            Assert.Equal(Encoding.UTF8.GetBytes(title), h.Remote.Fixture.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id",
                row => row.Blob(0), default, new SqlArg("$id", child)));
            Assert.Equal(title, h.Remote.Sessions.Get(child)!.Title);
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(command, EngineJson.Default.StartSession), h.Remote.Fixture.Db.QueryFirst(
                "SELECT CAST(payload_json AS BLOB) FROM engine_outbox WHERE id=$id", row => row.Blob(0), default, new SqlArg("$id", commandId.ToString("N") + "-0")));
            Assert.Equal(1, h.Remote.Fixture.Count("engine_outbox"));
            Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
            Assert.Equal(0, launcher.ProviderCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Ready(TwoHosts h)
    {
        var mirror = Assert.Single(h.Connections.CaptureMirrorOwners()).Mirror;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => ready.TrySetResult();
        mirror.CaughtUp += CaughtUp;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is null) await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { mirror.CaughtUp -= CaughtUp; }
    }

}
