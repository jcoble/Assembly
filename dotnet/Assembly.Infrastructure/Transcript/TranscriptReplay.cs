using System.Globalization;
using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.V2;
using Assembly.Domain.Events;

namespace Assembly.Infrastructure.Transcript;

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
        string? promptSession = null;
        var turns = 0;
        var readers = new Dictionary<string, UpdateReader>();
        foreach (var line in File.ReadLines(path))
        {
            using var entry = JsonDocument.Parse(line);
            var at = Ms(entry.RootElement.GetProperty("t").GetString()!);
            var direction = entry.RootElement.GetProperty("dir").GetString();
            var message = entry.RootElement.GetProperty("line");
            if (direction == "in" && message.TryGetProperty("result", out var initialized)
                && initialized.TryGetProperty("protocolVersion", out var version) && version.GetInt32() != AcpProtocol.Version)
                throw new InvalidDataException("Transcript replay requires ACP v2; re-record this v1 fixture.");
            switch (direction)
            {
                case "out" when MethodOf(message) == AcpMethods.SessionPrompt:
                    openPrompt = message.GetProperty("id").GetInt64();
                    promptSession = message.GetProperty("params").GetProperty("sessionId").GetString();
                    var text = message.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString()!;
                    events.Add(new TurnStarted(at, $"run-{++turns}", text));
                    break;
                case "in" when MethodOf(message) == AcpMethods.SessionUpdate:
                    var update = message.GetProperty("params").Deserialize(AcpJsonContext.Default.SessionNotification)!;
                    if (!readers.TryGetValue(update.SessionId, out var reader))
                        readers[update.SessionId] = reader = new UpdateReader();
                    events.AddRange(reader.Read(update, at).Where(e => e is not TurnStarted and not SessionMessage));
                    if (events.LastOrDefault() is TurnEnded or TurnFailed) openPrompt = null;
                    break;
                case "in" when openPrompt is { } id && message.TryGetProperty("id", out var replyId) && replyId.GetInt64() == id:
                    if (message.TryGetProperty("result", out var response) && !response.TryGetProperty("messageId", out _))
                        throw new InvalidDataException("An ACP v2 prompt response must acknowledge its messageId.");
                    if (message.TryGetProperty("error", out var error))
                    {
                        if (promptSession is { } native && readers.TryGetValue(native, out var promptReader))
                            events.AddRange(promptReader.RetainOutstanding(at));
                        events.Add(UpdateReader.TurnFailure(JsonRpcException.FromError(error), at));
                        openPrompt = null;
                    }
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
