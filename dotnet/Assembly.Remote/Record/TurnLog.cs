using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Remote.Record;

/// <summary>What one turn streamed: update counts, message text in arrival order, and a signal for the first nonempty text.</summary>
public sealed class TurnLog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _kinds = [];
    private readonly Dictionary<string, StringBuilder> _messages = [];
    private readonly List<string> _messageOrder = [];
    private readonly TaskCompletionSource _firstText = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstText => _firstText.Task;

    public string Text
    {
        get
        {
            lock (_gate)
                return string.Concat(_messageOrder.Select(id => _messages[id].ToString()));
        }
    }

    public string KindSummary
    {
        get
        {
            lock (_gate)
                return string.Join(", ", _kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value}"));
        }
    }

    public void Add(SessionNotification update)
    {
        var kind = update.Kind;
        var nonempty = false;
        lock (_gate)
        {
            _kinds[kind] = _kinds.GetValueOrDefault(kind) + 1;
            if (kind is not ("agent_message_chunk" or "agent_message")
                || !update.Update.TryGetProperty("messageId", out var id) || id.ValueKind != JsonValueKind.String)
                return;
            var messageId = id.GetString()!;
            if (!_messages.TryGetValue(messageId, out var message))
            {
                _messages[messageId] = message = new StringBuilder();
                _messageOrder.Add(messageId);
            }
            if (!update.Update.TryGetProperty("content", out var content)) return;
            if (kind == "agent_message" && content.ValueKind is JsonValueKind.Array or JsonValueKind.Null)
            {
                message.Clear();
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (TextOf(block) is { Length: > 0 } text)
                        {
                            message.Append(text);
                            nonempty = true;
                        }
            }
            else if (kind == "agent_message_chunk" && TextOf(content) is { Length: > 0 } chunk)
            {
                message.Append(chunk);
                nonempty = true;
            }
        }
        if (nonempty) _firstText.TrySetResult();
    }

    private static string? TextOf(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object
        && block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text"
        && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
}
