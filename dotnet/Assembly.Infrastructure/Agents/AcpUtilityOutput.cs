using System.Text;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Common.Agents;
using Assembly.Domain.Events;

namespace Assembly.Infrastructure.Agents;

/// <summary>One utility session's ordered ACP text, including v2 message replacement.</summary>
internal sealed class AcpUtilityOutput(string session)
{
    private const int Limit = 64 * 1024;
    private readonly UpdateReader _reader = new();
    private readonly Dictionary<string, string> _messages = [];
    private readonly List<string> _order = [];
    private int _bytes;
    internal string? Failure { get; private set; }
    internal bool Ended { get; private set; }

    internal void Read(SessionNotification update)
    {
        if (update.SessionId != session || Failure is not null) return;
        if (update.Kind is "tool_call_update" or "tool_call_content_chunk" or "terminal_update" or "terminal_output_chunk"
            or "subagent_update" or "session_message" or "session_message_chunk")
        { Failure = "utility_used_tools"; return; }
        foreach (var item in _reader.Read(update))
        {
            if (item is TextChunk chunk)
            {
                if (string.IsNullOrEmpty(chunk.MessageId)) { Failure = "utility_protocol"; return; }
                var prior = _messages.GetValueOrDefault(chunk.MessageId, "");
                var text = chunk.Replace ? chunk.Text : prior + chunk.Text;
                var bytes = Encoding.UTF8.GetByteCount(text);
                var total = _bytes - Encoding.UTF8.GetByteCount(prior) + bytes;
                var messageCount = _order.Count + (_messages.ContainsKey(chunk.MessageId) ? 0 : 1);
                if (total + Math.Max(0, messageCount - 1) * 2 > Limit)
                { Failure = "utility_output_limit"; return; }
                if (!_messages.ContainsKey(chunk.MessageId)) _order.Add(chunk.MessageId);
                _messages[chunk.MessageId] = text;
                _bytes = total;
            }
            else if (item is TurnFailed failed) { Failure = failed.Code == "-32000" ? "utility_auth_required" : "utility_failed"; return; }
            else if (item is TurnEnded) Ended = true;
        }
    }

    internal UtilityResult Result()
    {
        var text = string.Join("\n\n", _order.Select(id => _messages[id])).Trim();
        return Failure is { } reason ? new(UtilityStatus.Failed, null, reason)
            : text.Length == 0 ? new(UtilityStatus.Failed, null, "utility_empty")
            : new(UtilityStatus.Succeeded, text, null);
    }
}
