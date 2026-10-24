using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assembly.Engine.Transcript;

/// <summary>
/// Writes every line that crosses the agent's pipes to a JSONL file, one object per line:
/// <c>{"t":"…","dir":"out|in|err|exit","line":…}</c>. JSON lines are embedded as-is; stderr lines as strings;
/// the process's exit as its exit code. The headers of the MCP servers a session joins (its token for Assembly's
/// server) are written as <c>[redacted]</c>.
/// </summary>
public sealed class TranscriptLog : IDisposable
{
    private readonly TextWriter _writer;
    private readonly Lock _gate = new();

    public TranscriptLog(TextWriter writer) => _writer = writer;

    public static TranscriptLog ToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        return new TranscriptLog(stream);
    }

    public static TranscriptLog Discard() => new(TextWriter.Null);

    public void Sent(string jsonLine) => Write("out", jsonLine.Contains("\"mcpServers\"", StringComparison.Ordinal) ? Redacted(jsonLine) : jsonLine, isJson: true);

    // The line with every mcpServers header's value replaced.
    private static string Redacted(string jsonLine)
    {
        if (JsonNode.Parse(jsonLine) is not JsonObject message || message["params"]?["mcpServers"] is not JsonArray servers)
            return jsonLine;
        foreach (var header in servers.OfType<JsonObject>().SelectMany(server => server["headers"] as JsonArray ?? []).OfType<JsonObject>())
            header["value"] = "[redacted]";
        return message.ToJsonString();
    }

    public void Received(string jsonLine) => Write("in", jsonLine, isJson: true);

    public void AgentStderr(string text) => Write("err", text, isJson: false);

    public void AgentExited(int exitCode) => Write("exit", exitCode.ToString(CultureInfo.InvariantCulture), isJson: true);

    private void Write(string direction, string line, bool isJson)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", DateTimeOffset.UtcNow);
            json.WriteString("dir", direction);
            json.WritePropertyName("line");
            if (isJson && IsValidJson(line))
                json.WriteRawValue(line, skipInputValidation: true);
            else
                json.WriteStringValue(line);
            json.WriteEndObject();
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (_gate)
            _writer.WriteLine(text);
    }

    private static bool IsValidJson(string line)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
        try
        {
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }
}
