using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Acp;

/// <summary>
/// Plays a recorded version 1 adapter (<c>Fixtures/acp-v1</c>) against a client, line by line. The adapter's lines are
/// sent as recorded, with reply ids moved to the ids the client used; each client line is read and checked against
/// the recorded method. The client's answers to the adapter's own requests are kept in <see cref="Answers"/>.
/// </summary>
internal sealed class V1Recording(FakeAgent agent, string name)
{
    private readonly JsonObject[] _lines = Lines(name);
    private readonly Dictionary<long, JsonNode> _ids = [];
    private int _next;

    public List<JsonElement> Answers { get; } = [];

    /// <summary>The recorded result of the reply to request <paramref name="recordedId"/> in <paramref name="fixture"/>.</summary>
    public static JsonElement Result(string fixture, long recordedId) => Element(Lines(fixture).Where(line => Dir(line) == "in").Select(Line).First(line =>
        line["method"] is null && line["id"]?.GetValueKind() == JsonValueKind.Number && line["id"]!.GetValue<long>() == recordedId
        && line["result"] is not null)["result"]!);

    /// <summary>Plays up to and including the adapter's reply to the client's request <paramref name="recordedId"/>.</summary>
    public Task PlayToReplyAsync(long recordedId) => PlayAsync(line =>
        Dir(line) == "in" && Line(line)["method"] is null && Line(line)["id"]!.GetValue<long>() == recordedId, inclusive: true);

    /// <summary>Plays up to, not including, the client's next <paramref name="method"/>, so the test can make that call.</summary>
    public Task PlayToClientAsync(string method) => PlayAsync(line =>
        Dir(line) == "out" && Line(line)["method"]?.GetValue<string>() == method, inclusive: false);

    private async Task PlayAsync(Func<JsonObject, bool> stop, bool inclusive)
    {
        while (_next < _lines.Length)
        {
            var line = _lines[_next];
            if (Dir(line) == "exit") return;
            if (Dir(line) == "err")
            {
                _next++;
                continue;
            }
            if (!inclusive && stop(line)) return;
            _next++;
            if (Dir(line) == "in") await agent.SendAsync(Remapped(Line(line)).ToJsonString());
            else await ReadClientAsync(Line(line));
            if (inclusive && stop(line)) return;
        }
        throw new InvalidOperationException($"{name} ended before the expected line.");
    }

    private async Task ReadClientAsync(JsonObject recorded)
    {
        var sent = await agent.ReadAsync();
        if (recorded["method"] is { } method)
        {
            Assert.Equal(method.GetValue<string>(), sent.GetProperty("method").GetString());
            if (recorded["id"] is { } id) _ids[id.GetValue<long>()] = JsonNode.Parse(sent.GetProperty("id").GetRawText())!;
            return;
        }
        // An answer to the adapter's own request: same id, as the adapter numbered it.
        Assert.Equal(recorded["id"]!.ToJsonString(), sent.GetProperty("id").GetRawText());
        Answers.Add(sent.GetProperty("result").Clone());
    }

    private JsonObject Remapped(JsonObject line)
    {
        if (line["method"] is not null || line["id"] is not { } id) return line;
        var copy = line.DeepClone().AsObject();
        copy["id"] = _ids[id.GetValue<long>()].DeepClone();
        return copy;
    }

    /// <summary>A fixture's lines; <paramref name="fixture"/> is relative to <c>Fixtures</c>, such as <c>acp-v1/agy-main.jsonl</c>.</summary>
    private static JsonObject[] Lines(string fixture) => [.. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))
        .Select(line => JsonNode.Parse(line)!.AsObject())];

    private static string Dir(JsonObject line) => line["dir"]!.GetValue<string>();

    private static JsonObject Line(JsonObject line) => line["line"]!.AsObject();

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
