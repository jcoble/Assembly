using Assembly.Engine.Transcript;

namespace Assembly.Tests.Transcript;

public sealed class TranscriptLogTests
{
    [Fact]
    public void A_recorded_session_new_carries_no_Authorization_value()
    {
        var writer = new StringWriter();
        using (var log = new TranscriptLog(writer))
            log.Sent("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/w","mcpServers":[{"type":"http","name":"assembly","url":"http://127.0.0.1:1/mcp","headers":[{"name":"Authorization","value":"Bearer secret-token"}]}]}}""");

        var line = writer.ToString();
        Assert.DoesNotContain("secret-token", line);
        Assert.Contains("[redacted]", line);
        Assert.Contains("\"Authorization\"", line);
    }
}
