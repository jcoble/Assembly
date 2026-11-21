using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// The tool calls a version 1 turn left pending or in progress. Version 2 closes its tools before going idle; version 1
/// may end a turn with one still open, as on a stop during a long command, and an open tool keeps the session busy.
/// So the turn's end closes them: cancelled on a stop, failed otherwise. Read and closed on the reader thread.
/// After a stop, the agent's own "failed" for a tool that was still open reads as cancelled: Antigravity reports the tool
/// waiting on an approval failed ("Rejected by user") as soon as it gets session/cancel.
/// </summary>
internal sealed class OpenTools
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _bySession = [];
    private readonly HashSet<string> _stopping = [];

    /// <summary>Notes that session/cancel is going out for the session's turn; cleared when the turn closes.</summary>
    public void Stopping(string sessionId)
    {
        lock (_gate) _stopping.Add(sessionId);
    }

    /// <summary>
    /// Notes a version 1 tool update as received: a new tool with no status is pending; a status of pending or
    /// in_progress keeps it open; completed, failed or cancelled closes it. Returns the update to use, which differs only
    /// when a stop turns an open tool's "failed" into "cancelled".
    /// </summary>
    public SessionNotification Read(SessionNotification received)
    {
        if (received.Kind is not ("tool_call" or "tool_call_update") || received.Update.ValueKind != JsonValueKind.Object
            || !received.Update.TryGetProperty("toolCallId", out var id) || id.GetString() is not { Length: > 0 } toolCallId) return received;
        var status = received.Update.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : received.Kind == "tool_call" ? "pending" : null;
        if (status is null) return received;
        bool stopped;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(received.SessionId, out var open)) _bySession[received.SessionId] = open = [];
            if (status is "pending" or "in_progress")
            {
                open.Add(toolCallId);
                return received;
            }
            stopped = open.Remove(toolCallId) && status == "failed" && _stopping.Contains(received.SessionId);
        }
        if (!stopped) return received;
        var update = JsonNode.Parse(received.Update.GetRawText())!.AsObject();
        update["status"] = "cancelled";
        return received with { Update = UpdateNormalizer.Element(update) };
    }

    /// <summary>The updates that close the session's open tools at the end of its turn.</summary>
    public IReadOnlyList<SessionNotification> Close(string sessionId, string stopReason)
    {
        string[] open;
        lock (_gate)
        {
            _stopping.Remove(sessionId);
            if (!_bySession.Remove(sessionId, out var tools)) return [];
            open = [.. tools];
        }
        var status = stopReason == "cancelled" ? "cancelled" : "failed";
        return [.. open.Select(id => new SessionNotification(sessionId, JsonSerializer.SerializeToElement(
            new ToolClosed("tool_call_update", id, status), AcpJsonContext.Default.ToolClosed)))];
    }
}

/// <summary>A version 1 tool the turn's end closed.</summary>
internal sealed record ToolClosed(string SessionUpdate, string ToolCallId, string Status);
