using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Tests.Acp;

public sealed class AcpV2MessagesTests
{
    // Codex 2.5.0 initialize, reduced to the fields this contract consumes; Task 0 live receipt.
    private const string CodexInitialize = """{"protocolVersion":2,"info":{"name":"@agentclientprotocol/codex-acp","version":"2.5.0"},"capabilities":{"session":{"delete":{}}},"authMethods":[],"_meta":{"steering":{"supported":true}}}""";

    [Fact]
    public void Initialize_request_is_version_2_without_air()
    {
        var json = JsonSerializer.SerializeToElement(InitializeRequest.ForAssembly("assembly", "1.0"), AcpJsonContext.Default.InitializeRequest);
        Assert.Equal(2, json.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("assembly", json.GetProperty("info").GetProperty("name").GetString());
        var capabilities = json.GetProperty("capabilities");
        Assert.False(capabilities.TryGetProperty("_meta", out _));
        Assert.False(capabilities.TryGetProperty("fs", out _));
        Assert.False(capabilities.TryGetProperty("terminal", out _));
        Assert.Equal("{}", capabilities.GetProperty("auth").GetProperty("terminal").GetRawText());
        var terminal = JsonSerializer.SerializeToElement(ClientCapabilities.WithTerminalAuthentication(true), AcpJsonContext.Default.ClientCapabilities);
        Assert.Equal("{}", terminal.GetProperty("auth").GetProperty("terminal").GetRawText());
        var unavailable = JsonSerializer.SerializeToElement(ClientCapabilities.WithTerminalAuthentication(false), AcpJsonContext.Default.ClientCapabilities);
        Assert.False(unavailable.GetProperty("auth").TryGetProperty("terminal", out _));
        Assert.Equal("{}", capabilities.GetProperty("elicitation").GetProperty("url").GetRawText());
    }

    [Theory]
    [InlineData(CodexInitialize, true)]
    [InlineData("""{"protocolVersion":2,"capabilities":{},"_meta":{"steering":{"supported":false}}}""", false)]
    [InlineData("""{"protocolVersion":2,"capabilities":{}}""", false)]
    public void CanSteer_reads_meta_steering(string line, bool expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize(line, AcpJsonContext.Default.InitializeResponse)!.CanSteer);

    [Fact]
    public void Delete_is_an_advertised_session_capability() =>
        Assert.True(JsonSerializer.Deserialize(CodexInitialize, AcpJsonContext.Default.InitializeResponse)!.CanDelete);

    [Fact]
    public void Prompt_response_is_message_id() =>
        Assert.Equal("m-1", JsonSerializer.Deserialize("""{"messageId":"m-1"}""", AcpJsonContext.Default.PromptResponse)!.MessageId);

    [Fact]
    public void State_update_reads_stop_reason_and_error()
    {
        var state = JsonSerializer.Deserialize("""{"state":"idle","stopReason":"error","error":{"code":-32603,"message":"failed","data":{"detail":"x"}}}""", AcpJsonContext.Default.StateUpdate)!;
        Assert.Equal("error", state.StopReason);
        Assert.Equal(-32603, state.Error!.Code);
    }

    [Fact]
    public void Permission_v2_has_title_and_subject()
    {
        var request = JsonSerializer.Deserialize("""{"sessionId":"s","title":"Run ls","description":"Read directory","subject":{"type":"tool_call","toolCall":{"toolCallId":"t","title":"ls"}},"options":[{"optionId":"a","name":"Yes","kind":"allow_once"},{"optionId":"n","name":"No","kind":"reject_once"}]}""", AcpJsonContext.Default.RequestPermissionRequest)!;
        Assert.Equal("Run ls", request.Title);
        Assert.Equal("tool_call", request.Subject!.Type);
        Assert.Equal(2, request.Options.Count);
    }
    [Fact]
    public void Observed_protocol_2_quota_metadata_preserves_turn_token_counts()
    {
        var state = JsonSerializer.Deserialize("""{"state":"idle","stopReason":"end_turn","_meta":{"quota":{"token_count":{"totalTokens":14601,"inputTokens":2308,"cachedInputTokens":12288,"cacheWriteInputTokens":0,"outputTokens":5,"reasoningOutputTokens":0}}}}""",
            AcpJsonContext.Default.StateUpdate)!;
        Assert.Equal((2308L, 5L, 12288L, 0L), (state.ReportedUsage!.InputTokens, state.ReportedUsage.OutputTokens,
            state.ReportedUsage.CachedReadTokens, state.ReportedUsage.CachedWriteTokens));
    }

}
