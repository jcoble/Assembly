using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AcpUtilityOutputTests
{
    [Fact]
    public void Message_upserts_replace_prior_chunks_without_duplicating_the_answer()
    {
        var output = new AcpUtilityOutput("owned");
        output.Read(Update("agent_message_chunk", "answer", "part"));
        output.Read(Update("agent_message", "answer", "Complete"));
        output.Read(Update("agent_message_chunk", "answer", " answer"));
        output.Read(Update("agent_message", "other", "Second"));
        Assert.Equal(new UtilityResult(UtilityStatus.Succeeded, "Complete answer\n\nSecond", null), output.Result());
    }

    [Fact]
    public void Thoughts_and_foreign_session_text_are_never_the_answer()
    {
        var output = new AcpUtilityOutput("owned");
        output.Read(Update("agent_thought_chunk", "thought", "private"));
        output.Read(Update("agent_message_chunk", "answer", "foreign") with { SessionId = "other" });
        Assert.Equal(UtilityStatus.Failed, output.Result().Status);
        Assert.Equal("utility_empty", output.Result().Reason);
    }

    [Theory]
    [InlineData("tool_call_update")]
    [InlineData("terminal_update")]
    [InlineData("subagent_update")]
    [InlineData("session_message")]
    public void Actual_tool_or_native_activity_prevents_a_successful_draft(string kind)
    {
        var output = new AcpUtilityOutput("owned");
        output.Read(Update("agent_message_chunk", "answer", "plausible answer"));
        using var json = JsonDocument.Parse($$"""{"sessionUpdate":"{{kind}}"}""");
        output.Read(new("owned", json.RootElement.Clone()));
        Assert.Equal("utility_used_tools", output.Result().Reason);
        Assert.Equal(UtilityStatus.Failed, output.Result().Status);
    }

    [Fact]
    public void The_output_limit_counts_utf8_bytes_and_rejects_instead_of_truncating()
    {
        var output = new AcpUtilityOutput("owned");
        output.Read(Update("agent_message_chunk", "answer", new string('é', 32769)));
        Assert.Equal("utility_output_limit", output.Result().Reason);
        Assert.Null(output.Result().Text);
    }

    private static SessionNotification Update(string kind, string id, string text)
    {
        var json = kind is "agent_message" or "agent_thought"
            ? $$$"""{"sessionUpdate":"{{{kind}}}","messageId":"{{{id}}}","content":[{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}]}"""
            : $$$"""{"sessionUpdate":"{{{kind}}}","messageId":"{{{id}}}","content":{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}}""";
        using var parsed = JsonDocument.Parse(json);
        return new("owned", parsed.RootElement.Clone());
    }
}
