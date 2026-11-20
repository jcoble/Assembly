using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// The tool calls a version 1 turn left pending or in progress. Version 2 closes its tools before going idle; version 1
/// may end a turn with one still open, as on a stop during a long command, and an open tool keeps the session busy.
/// So the turn's end closes them: cancelled on a stop, failed otherwise. Read and closed on the reader thread.
/// </summary>
internal sealed class OpenTools
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _bySession = [];

    /// <summary>
    /// Notes a version 1 tool update as received: a new tool with no status is pending; a status of pending or
    /// in_progress keeps it open; completed, failed or cancelled closes it.
    /// </summary>
    public void Read(SessionNotification received)
    {
        if (received.Kind is not ("tool_call" or "tool_call_update") || received.Update.ValueKind != JsonValueKind.Object
            || !received.Update.TryGetProperty("toolCallId", out var id) || id.GetString() is not { Length: > 0 } toolCallId) return;
        var status = received.Update.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : received.Kind == "tool_call" ? "pending" : null;
        if (status is null) return;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(received.SessionId, out var open)) _bySession[received.SessionId] = open = [];
            if (status is "pending" or "in_progress") open.Add(toolCallId);
            else open.Remove(toolCallId);
        }
    }

    /// <summary>The updates that close the session's open tools at the end of its turn.</summary>
    public IReadOnlyList<SessionNotification> Close(string sessionId, string stopReason)
    {
        string[] open;
        lock (_gate)
        {
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
