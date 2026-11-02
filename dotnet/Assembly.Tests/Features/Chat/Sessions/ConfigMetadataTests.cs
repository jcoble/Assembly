using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class ConfigMetadataTests
{
    [Fact]
    public async Task Metadata_extraction_preserves_real_replay_and_tool_updates_in_their_original_order()
    {
        var pipe = Channel.CreateUnbounded<SessionNotification>();
        var router = new UpdateRouter(pipe.Reader);
        try
        {
            await pipe.Writer.WriteAsync(Update("agent_message_chunk", "\"content\":{\"type\":\"text\",\"text\":\"before\"}"));
            await pipe.Writer.WriteAsync(Update("available_commands_update", "\"availableCommands\":[{\"name\":\"actual\"}]"));
            await pipe.Writer.WriteAsync(Update("config_option_update", "\"configOptions\":[{\"id\":\"actual\",\"custom\":true}]"));
            await pipe.Writer.WriteAsync(Update("tool_call", "\"toolCallId\":\"tool-1\""));
            var result = router.Metadata("native", default, null);
            Assert.Equal("actual", result.Options[0].GetProperty("id").GetString());
            using var commands = JsonDocument.Parse(result.Commands!);
            Assert.Equal("actual", commands.RootElement[0].GetProperty("name").GetString());
            var remaining = router.For("native");
            Assert.True(remaining.TryRead(out var first));
            Assert.NotNull(first);
            Assert.Equal("agent_message_chunk", first.Kind);
            Assert.True(remaining.TryRead(out var second));
            Assert.NotNull(second);
            Assert.Equal("tool_call", second.Kind);
            Assert.False(remaining.TryRead(out _));
        }
        finally { pipe.Writer.TryComplete(); await router.Completion; }
    }

    [Fact]
    public async Task Complete_ACP2_response_metadata_is_authoritative_over_preceding_notifications()
    {
        var pipe = Channel.CreateUnbounded<SessionNotification>();
        var router = new UpdateRouter(pipe.Reader);
        try
        {
            await pipe.Writer.WriteAsync(Update("available_commands_update", "\"availableCommands\":[{\"name\":\"earlier\"}]"));
            await pipe.Writer.WriteAsync(Update("config_option_update", "\"configOptions\":[{\"id\":\"earlier\"}]"));
            using var options = JsonDocument.Parse("[{\"id\":\"response\",\"unknown\":{\"kept\":true}}]");
            const string commands = "[{\"name\":\"response\",\"custom\":true}]";
            var result = router.Metadata("native", options.RootElement, commands);
            Assert.Equal("response", result.Options[0].GetProperty("id").GetString());
            Assert.True(result.Options[0].GetProperty("unknown").GetProperty("kept").GetBoolean());
            Assert.Equal(commands, result.Commands);
        }
        finally { pipe.Writer.TryComplete(); await router.Completion; }
    }

    private static SessionNotification Update(string kind, string fields)
    {
        using var json = JsonDocument.Parse("{\"sessionUpdate\":\"" + kind + "\"," + fields + "}");
        return new("native", json.RootElement.Clone());
    }
}
