using System.Globalization;
using System.Text.Json;
using Assembly.Engine.Acp;
using Assembly.Engine.Acp.JsonRpc;
using Assembly.Engine.Acp.V1;
using Assembly.Engine.Events;

namespace Assembly.Engine.Transcript;

/// <summary>
/// A transcript (see <see cref="TranscriptLog"/>) replayed as the events the live host would make from it: the prompt we sent,
/// each update, and the prompt's result, error, or the agent going away while it was open.
/// </summary>
public static class TranscriptReplay
{
    public static IReadOnlyList<AgentEvent> Events(string path)
    {
        var events = new List<AgentEvent>();
        long? openPrompt = null;
        var turns = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var entry = JsonDocument.Parse(line);
            var at = Ms(entry.RootElement.GetProperty("t").GetString()!);
            var direction = entry.RootElement.GetProperty("dir").GetString();
            var message = entry.RootElement.GetProperty("line");
            switch (direction)
            {
                case "out" when MethodOf(message) == AcpMethods.SessionPrompt:
                    openPrompt = message.GetProperty("id").GetInt64();
                    var text = message.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString()!;
                    events.Add(new TurnStarted(at, $"run-{++turns}", text));
                    break;
                case "in" when MethodOf(message) == AcpMethods.SessionUpdate:
                    var update = message.GetProperty("params").Deserialize(AcpJsonContext.Default.SessionNotification)!;
                    events.AddRange(UpdateReader.ReadAll(update, at));
                    break;
                case "in" when openPrompt is { } id && message.TryGetProperty("id", out var replyId) && replyId.GetInt64() == id:
                    events.Add(message.TryGetProperty("result", out var result)
                        ? UpdateReader.TurnEnd(result.Deserialize(AcpJsonContext.Default.PromptResponse)!, at)
                        : UpdateReader.TurnFailure(JsonRpcException.FromError(message.GetProperty("error")), at));
                    openPrompt = null;
                    break;
                case "exit" when openPrompt is not null:
                    events.Add(new AgentLost(at));
                    openPrompt = null;
                    break;
            }
        }

        return events;
    }

    public static long Ms(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string? MethodOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var method) ? method.GetString() : null;
}
