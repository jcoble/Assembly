using System.Text.Json;
using Assembly.Domain.Events;
using Assembly.Infrastructure.Transcript;

namespace Assembly.Tests.Acp;

public sealed class TranscriptV2Tests
{
    private static IReadOnlyList<AgentEvent> Replay(params string[] lines)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, lines);
            return TranscriptReplay.Events(path);
        }
        finally { File.Delete(path); }
    }

    private static string Entry(string direction, string json) => JsonSerializer.Serialize(new
    {
        t = "2026-11-01T15:25:00Z",
        dir = direction,
        line = JsonDocument.Parse(json).RootElement
    });

    [Fact]
    public void Prompt_acknowledgement_is_not_the_turn_end()
    {
        var events = Replay(
            Entry("out", """{"id":1,"method":"session/prompt","params":{"sessionId":"s","prompt":[{"type":"text","text":"hi"}]}}"""),
            Entry("in", """{"id":1,"result":{"messageId":"m"}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"running"}}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","messageId":"a","content":{"type":"text","text":"hi"}}}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}}}"""));
        Assert.Collection(events, e => Assert.IsType<TurnStarted>(e), e => Assert.IsType<TextChunk>(e), e => Assert.IsType<TurnEnded>(e));
    }

    [Fact]
    public void Initial_session_commands_replay_without_chat_rows()
    {
        var events = Replay(Entry("in", """{"id":1,"result":{"sessionId":"s","availableCommands":[{"name":"status","input":null}]}}"""));
        var commands = Assert.IsType<CommandsChanged>(Assert.Single(events));
        Assert.Equal("status", commands.Commands[0].GetProperty("name").GetString());
        Assert.Empty(new Assembly.Application.Features.Chat.Rows.RowConverter().Apply(commands).Rows);
    }

    [Fact]
    public void Idle_with_usage_then_clean_exit_does_not_lose_a_completed_turn()
    {
        var events = Replay(
            Entry("out", """{"id":1,"method":"session/prompt","params":{"sessionId":"s","prompt":[{"type":"text","text":"hi"}]}}"""),
            Entry("in", """{"id":1,"result":{"messageId":"m"}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"running"}}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","usage":{"inputTokens":2,"outputTokens":3}}}}"""),
            Entry("exit", "0"));

        Assert.Single(events.OfType<TurnEnded>());
        Assert.Single(events.OfType<UsageReported>());
        Assert.Empty(events.OfType<AgentLost>());
    }

    [Fact]
    public void Child_idle_does_not_hide_the_parent_being_lost()
    {
        var events = Replay(
            Entry("out", """{"id":1,"method":"session/prompt","params":{"sessionId":"parent","prompt":[{"type":"text","text":"hi"}]}}"""),
            Entry("in", """{"id":1,"result":{"messageId":"m"}}"""),
            Entry("in", """{"method":"session/update","params":{"sessionId":"child","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}}}"""),
            Entry("exit", "1"));

        Assert.Single(events.OfType<AgentLost>());
    }

    [Fact]
    public void V1_transcripts_are_refused_instead_of_partially_replayed() =>
        Assert.Throws<InvalidDataException>(() => Replay(Entry("in", """{"id":1,"result":{"protocolVersion":1,"agentCapabilities":{}}}""")));
}
