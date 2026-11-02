using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class CrossMachineStartTests
{
    [Fact]
    public async Task Window_child_of_actual_source_parent_is_started_by_coordinator_on_target()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        var child = Guid.NewGuid().ToString();
        using var socket = await f.Window();
        var command = new WindowCommand(Guid.NewGuid(), child, 0, 0,
            new StartSession("codex", "model", f.DirectFolder, "Child", f.Parent, null, null), h.Remote.Fixture.Machine);
        var args = JsonSerializer.Serialize(command, EngineSocketJson.Default.WindowCommand);
        await Send(socket, $"{{\"t\":\"call\",\"id\":1,\"feature\":\"engine\",\"method\":\"command\",\"args\":{args}}}");
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var result = frame.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!;
        Assert.True(result.Applied, result.Error);
        Assert.Equal("starting", h.Remote.Fixture.State("engine_session", child));
        Assert.Equal(f.Parent, h.Remote.Fixture.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM session_info WHERE session_id=$id", new SqlArg("$id", child)));
        Assert.Null(h.Mac.Fixture.State("engine_session", child));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_success_has_actual_target_child_and_durable_full_source_prompt_with_real_repository_policy(bool direct)
    {
        var title = new string('T', 35000) + "\0AFTER_TITLE";
        await using var f = await StartHosts.Open(title: title); var h = f.H;
        var answer = await new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "TASK\0AFTER_TASK", Title: "Child", Folder: direct ? f.DirectFolder : null, Machine: h.Remote.Runtime.Machine), default);
        Assert.Equal("starting", h.Remote.Fixture.State("engine_session", answer.SessionId));
        Assert.Equal("source-model", h.Remote.Fixture.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal(direct ? 0 : 1, h.Remote.Fixture.Number("SELECT managed_checkout FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal(direct ? f.DirectFolder : Path.Combine(f.Root, "remote", f.Parent),
            h.Remote.Fixture.Text("SELECT folder FROM engine_session WHERE id=$id", new SqlArg("$id", answer.SessionId)));
        Assert.Equal(direct ? f.DirectFolder : Path.Combine(f.Root, "mac"),
            h.Remote.Fixture.Text("SELECT repo_url FROM session_info WHERE session_id=$id", new SqlArg("$id", answer.SessionId)));
        var message = h.Mac.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB),CAST(sender_label AS BLOB),sender_kind,from_session_id,to_session_id,state FROM engine_message WHERE id=$id",
            row => (Body: Encoding.UTF8.GetString(row.Blob(0)), Label: Encoding.UTF8.GetString(row.Blob(1)), Kind: row.Text(2), From: row.Text(3), To: row.Text(4), State: row.Text(5)), default, new SqlArg("$id", answer.MessageId));
        Assert.StartsWith($"Session \"{title}\" ({f.Parent}) started you.", message.Body);
        Assert.EndsWith("TASK\0AFTER_TASK", message.Body); Assert.Equal($"{title} ({f.Parent})", message.Label);
        Assert.Equal("session", message.Kind); Assert.Equal(f.Parent, message.From); Assert.Equal(answer.SessionId, message.To); Assert.Equal("queued", message.State);
        Assert.Equal(1, h.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, h.Remote.Fixture.Count("engine_message")); // Source commit wakes the separately owned relay; no blind target prompt.
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Fact]
    public async Task Actual_current_caller_epoch_is_checked_before_any_target_prerequisite()
    {
        await using var f = await StartHosts.Open(); var h = f.H;
        h.Mac.Fixture.Db.Writer.Write("test.move", scope => scope.Execute("UPDATE engine_session SET epoch=1 WHERE id=$id", new SqlArg("$id", f.Parent)));
        var error = await Assert.ThrowsAsync<McpRefused>(() => new McpMachineStarts(f.Starts).StartAsync(new(f.Parent, 0),
            new(Prompt: "Task", Title: "Child", Machine: h.Remote.Runtime.Machine), default));
        Assert.Equal("stale_epoch", error.Refusal.Code);
        Assert.Equal(0, h.Mac.Fixture.Count("engine_message"));
        Assert.Null(h.Remote.Fixture.State("engine_session", f.Parent));
    }

    private static Task Send(ClientWebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, default);

    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType); bytes.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
}
