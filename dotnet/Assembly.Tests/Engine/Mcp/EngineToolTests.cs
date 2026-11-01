using System.Security.Claims;
using System.Text.Json;
using Assembly.Engine.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Tests.Engine.Mcp;

public sealed class EngineToolTests
{
    private sealed class Probe(Func<McpCaller, JsonElement, CancellationToken, ValueTask<JsonElement>> call)
        : EngineTool("probe", "Test tool", """{"type":"object","additionalProperties":false}""")
    {
        protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken) =>
            call(caller, arguments, cancellationToken);
    }

    [Fact]
    public async Task Sdk_invocation_uses_claims_and_returns_one_json_text_block()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
        using var cancelled = new CancellationTokenSource();
        using var forged = JsonDocument.Parse("\"forged\"");
        var tool = new Probe((caller, args, token) =>
        {
            Assert.Equal(new McpCaller("trusted", 7), caller);
            Assert.Equal(cancelled.Token, token);
            Assert.Equal("forged", args.GetProperty("sessionId").GetString());
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(caller, McpJson.Default.McpCaller));
        });
        var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(1) },
            new() { Name = "probe", Arguments = new Dictionary<string, JsonElement> { ["sessionId"] = forged.RootElement.Clone() } })
        { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", "trusted"), new("assembly:epoch", "7")], "test")) };
        var result = await tool.InvokeAsync(request, cancelled.Token);
        Assert.False(result.IsError);
        Assert.Equal("{\"sessionId\":\"trusted\",\"epoch\":7}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal("probe", tool.ProtocolTool.Name);
        Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("refused", "Your session moved to another machine; this call was not applied.")]
    [InlineData("json", "probe: Assembly refused these arguments.")]
    [InlineData("identity", "Your session has ended in Assembly; this call was not applied.")]
    public async Task Sdk_invocation_translates_refusals_and_bad_json(string mode, string expected)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
        var tool = new Probe((_, _, _) => mode == "refused" ? throw new McpRefused(new("stale_epoch")) : throw new JsonException());
        var request = new RequestContext<CallToolRequestParams>(server, new() { Method = "tools/call", Id = new RequestId(1) }, new() { Name = "probe" })
        { User = mode == "identity" ? null : new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", "s"), new("assembly:epoch", "0")], "test")) };
        var result = await tool.InvokeAsync(request);
        Assert.True(result.IsError);
        Assert.Equal(expected, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Generated_json_rejects_unknown_members() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("""{"sessionId":"s","epoch":0,"actor":"window"}""", McpJson.Default.McpCaller));
}
