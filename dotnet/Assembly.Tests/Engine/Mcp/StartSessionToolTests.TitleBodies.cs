using Assembly.Engine;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Mcp;

public sealed partial class StartSessionToolTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(40000)]
    public async Task Actual_helper_initial_message_keeps_full_caller_title_and_task_suffix_before_release(int prefix)
    {
        await using var h = new Harness(); h.Fixture.SeedSession("owner");
        var title = new string('x', prefix) + "\0AFTER_NUL😀";
        h.Fixture.Db.Writer.Write("test.existing-full-title", scope => scope.Execute(
            "UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='owner'", new SqlArg("$title", Encoding.UTF8.GetBytes(title))));
        var response = await h.Call(h.Start, "owner", """{"prompt":"TASK_SUFFIX","title":"Helper"}""");
        Assert.False(response.IsError, Text(response));
        using var answer = JsonDocument.Parse(Text(response));
        var child = answer.RootElement.GetProperty("sessionId").GetString()!;
        var message = answer.RootElement.GetProperty("messageId").GetString()!;
        var body = $"Session \"{title}\" (owner) started you. Do the task below. Your last reply each turn goes back to it as a message; you can also message it with the Assembly MCP server's send_message tool.\n\nTASK_SUFFIX";
        Assert.Equal(Encoding.UTF8.GetBytes(body), h.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id=$id",
            r => r.Blob(0), default, new SqlArg("$id", message)));
        Assert.Equal(1, h.Fixture.Number("SELECT framed FROM engine_message WHERE id=$id", new SqlArg("$id", message)));
        Assert.Equal(0, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        Assert.True(h.Fixture.Apply(new SetSessionState("idle", null, null), child,
            h.Fixture.Number("SELECT version FROM engine_session WHERE id=$id", new SqlArg("$id", child)), actor: new(ActorKind.System, "test")).Applied);
        var intent = JsonSerializer.Deserialize(h.Fixture.Text("SELECT payload_json FROM engine_outbox WHERE kind='send'")!, EngineJson.Default.DeliverMessage)!;
        Assert.Equal($"{title} (owner)", intent.SenderLabel);
        Assert.Equal($"[message from {title} (owner)]\n{body}", intent.Text);
        Assert.Equal(1, h.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
    }
    [Fact]
    public async Task Mcp_title_detail_uses_actual_caller_epoch_and_relationship_for_stat_and_pinned_chunks()
    {
        await using var h = new Harness(); h.Fixture.SeedSession("caller"); h.Fixture.SeedSession("child", parentId: "caller");
        h.Fixture.SeedSession("hidden", parentId: "child");
        var title = "\0" + new string('x', 1048575) + "😀suffix";
        Assert.True(h.Fixture.Db.Writer.Write("title", scope => new EngineCommands(h.Fixture.Machine, h.Fixture.Limits).ApplyPreparedSessionTitle(scope,
            new(Guid.NewGuid(), h.Fixture.Machine, new(ActorKind.System, "test"), "child", 0, 0, 1000),
            EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        var tool = new Assembly.Engine.Mcp.Tools.GetSessionTitleTool(h.Gate);
        var result = await h.Call(tool, "caller", """{"session":"child"}"""); Assert.False(result.IsError, Text(result));
        var stat = JsonSerializer.Deserialize(Text(result), Assembly.Engine.Mcp.McpJson.Default.SessionTitleStat)!;
        var read = await h.Call(tool, "caller", $"{{\"session\":\"child\",\"version\":{stat.Version},\"epoch\":0,\"totalBytes\":{stat.Utf8Bytes},\"offset\":0}}");
        Assert.False(read.IsError, Text(read));
        Assert.Equal(Encoding.UTF8.GetBytes(title)[..1048576], JsonSerializer.Deserialize(Text(read), Assembly.Engine.Mcp.McpJson.Default.SessionTitleChunk)!.Data);
        var sdk = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        var resultJson = JsonSerializer.Serialize(read, sdk.GetTypeInfo(typeof(ModelContextProtocol.Protocol.CallToolResult)));
        var rpcType = sdk.GetTypeInfo(typeof(ModelContextProtocol.Protocol.JsonRpcResponse));
        var rpc = JsonSerializer.Deserialize($"{{\"jsonrpc\":\"2.0\",\"id\":{long.MaxValue},\"result\":{resultJson}}}", rpcType)!;
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(rpc, rpcType).Length, 1398104, 4 * 1024 * 1024);
        Assert.True((await h.Call(tool, "caller", """{"session":"hidden"}""")).IsError);
        Assert.True((await h.Call(tool, "caller", """{"session":"child","machine":"elsewhere"}""")).IsError);
        Assert.True((await h.Call(tool, "caller", """{"session":"child","offset":0}""")).IsError);
    }

}
