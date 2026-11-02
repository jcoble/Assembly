using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Engine.Delivery;

public sealed class SenderIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_event_keeps_source_identity_separate_from_captured_label(bool steer)
    {
        foreach (var label in new[] { "Captured title (peer/x)", "Captured [\"😀\"]\0" + new string('λ', 40000) })
        {
            var json = JsonSerializer.Serialize(new { At = 1000, RunId = "run", Text = "body\0😀", ClientId = "c", SenderLabel = label, FromSessionId = "peer/x(y)" });
            var converter = new RowConverter();
            var batch = steer ? converter.Apply(JsonSerializer.Deserialize<TurnSteered>(json)!) : converter.Apply(JsonSerializer.Deserialize<TurnStarted>(json)!);
            var row = Assert.Single(batch.Rows, value => value.Kind == "user");
            Assert.Equal("peer/x(y)", row.Payload.GetProperty("from").GetString());
            Assert.Equal(label, row.Payload.GetProperty("senderLabel").GetString());
            Assert.Equal("body\0😀", row.Payload.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Optional_sender_label_round_trips_without_moving_acknowledgment_constructor_slot()
    {
        var old = new UserPayload("hi", "peer", "c");
        Assert.Equal("{\"text\":\"hi\",\"from\":\"peer\",\"clientId\":\"c\"}", JsonSerializer.Serialize(old, RowJsonContext.Rows.UserPayload));
        var ack = new UserAcknowledgment("prompt_inserted", "provider");
        Assert.Same(ack, new UserPayload("hi", "peer", "c", ack).Acknowledgment);
        var json = """{"text":"hi","from":"peer/x","clientId":"c","senderLabel":"Captured title (peer/x)"}""";
        var payload = JsonSerializer.Deserialize(json, RowJsonContext.Rows.UserPayload)!;
        Assert.Contains("\"senderLabel\":\"Captured title (peer/x)\"", JsonSerializer.Serialize(payload, RowJsonContext.Rows.UserPayload));
    }
    [Theory]
    [InlineData("Message old from another agent's session (session peer/x, \"Title\"), sent with the Assembly MCP server.\n\nbody", "peer/x")]
    [InlineData("[message from Captured title (peer/x)]\nbody", null)]
    public void Legacy_transcript_source_interpretation_is_presentation_only(string text, string? source)
    {
        var path = Path.GetTempFileName();
        try
        {
            var entry = new { t = "2026-01-01T00:00:00Z", dir = "out", line = new { jsonrpc = "2.0", id = 1, method = "session/prompt", @params = new { sessionId = "acp", prompt = new[] { new { type = "text", text } } } } };
            File.WriteAllText(path, JsonSerializer.Serialize(entry) + "\n");
            var started = Assert.Single(Assembly.Infrastructure.Transcript.TranscriptReplay.Events(path).OfType<TurnStarted>());
            Assert.Equal(source, started.FromSessionId); Assert.Null(started.SenderLabel);
            var user = Assert.Single(new RowConverter().Apply(started).Rows, row => row.Kind == "user");
            Assert.Equal(source, user.Payload.TryGetProperty("from", out var from) ? from.GetString() : null); Assert.False(user.Payload.TryGetProperty("senderLabel", out _));
        }
        finally { File.Delete(path); }
    }
}
