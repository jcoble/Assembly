using System.Text;
using System.Text.Json;
using Assembly.Engine.Acp.Messages;

namespace Assembly.Remote.Smoke;

/// <summary>What one turn streamed: a count per update kind, the agent's text, and a signal for the first text.</summary>
public sealed class TurnLog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _kinds = [];
    private readonly StringBuilder _text = new();
    private readonly TaskCompletionSource _firstText = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstText => _firstText.Task;

    public string Text
    {
        get
        {
            lock (_gate)
                return _text.ToString();
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
        var text = kind == "agent_message_chunk" ? TextOf(update.Update) : null;
        lock (_gate)
        {
            _kinds[kind] = _kinds.GetValueOrDefault(kind) + 1;
            if (text is not null)
                _text.Append(text);
        }
        if (text is not null)
            _firstText.TrySetResult();
    }

    private static string? TextOf(JsonElement update) =>
        update.TryGetProperty("content", out var content)
        && content.TryGetProperty("type", out var type) && type.GetString() == "text"
        && content.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;
}
