using System.IO.Pipelines;
using System.Text.Json;

namespace Assembly.Tests.Acp;

/// <summary>The agent's end of two in-memory pipes, scripted line by line from a test.</summary>
internal sealed class FakeAgent
{
    private readonly Pipe _toAgent = new();
    private readonly Pipe _toClient = new();
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public FakeAgent()
    {
        _reader = new StreamReader(_toAgent.Reader.AsStream());
        _writer = new StreamWriter(_toClient.Writer.AsStream()) { AutoFlush = true };
    }

    public Stream ClientReads => _toClient.Reader.AsStream();

    public Stream ClientWrites => _toAgent.Writer.AsStream();

    public async Task<JsonElement> ReadAsync()
    {
        var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    public Task SendAsync(string json) => _writer.WriteLineAsync(json);

    /// <summary>Answers <paramref name="request"/> with <paramref name="resultJson"/>, echoing its id.</summary>
    public Task ReplyAsync(JsonElement request, string resultJson) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetRawText()},\"result\":{resultJson}}}");

    public Task ReplyErrorAsync(JsonElement request, int code, string message) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetRawText()},\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}}}}}");

    public void CloseOutput() => _toClient.Writer.Complete();
}
