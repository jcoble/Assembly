using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// The tool calls of each session's version 1 turn, read and closed on the reader thread.
/// <list type="bullet">
/// <item>Version 1 may end a turn with a tool still open, as on a stop during a long command, and an open tool keeps the
/// session busy. So the turn's end closes them: cancelled on a stop, failed otherwise.</item>
/// <item>After a stop, the agent's own "failed" for a tool that was still open reads as cancelled: Antigravity reports
/// the tool waiting on an approval failed ("Rejected by user") as soon as it gets session/cancel.</item>
/// <item>An approved call runs as one: Antigravity asks about call A, and after an allow it runs the work as a new
/// call B, then reports A failed at the turn's end. The next call after the allow, with A's kind and A's first
/// location, is A's run: its progress and result go out under A's id, and A's own later report is dropped once A has
/// finished that way.</item>
/// </list>
/// </summary>
internal sealed class OpenTools
{
    private sealed record Approved(string ToolCallId, string? Kind, string Path);

    private sealed class Turn
    {
        public readonly HashSet<string> Open = [];
        public readonly Dictionary<string, string> RunOf = [];   // B's id -> A's id
        public readonly HashSet<string> RanThrough = [];         // A ids that finished through their run
        public Approved? Allowed;
        public bool Stopping;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Turn> _turns = [];

    /// <summary>Notes that session/cancel is going out for the session's turn; cleared when the turn closes.</summary>
    public void Stop(string sessionId)
    {
        lock (_gate) TurnOf(sessionId).Stopping = true;
    }

    /// <summary>Whether session/cancel went out for the session's turn and the turn has not closed yet.</summary>
    public bool IsStopping(string sessionId)
    {
        lock (_gate) return _turns.TryGetValue(sessionId, out var turn) && turn.Stopping;
    }

    /// <summary>
    /// Notes the answer to a version 1 permission request (<paramref name="parameters"/> as the agent sent it). Only an
    /// allow for a call that names a location can join the next call; any other answer clears what an earlier allow left.
    /// </summary>
    public void Answered(string sessionId, JsonElement parameters, bool allowed)
    {
        Approved? approved = null;
        if (allowed && parameters.TryGetProperty("toolCall", out var call) && call.ValueKind == JsonValueKind.Object
            && Text(call, "toolCallId") is { Length: > 0 } id && FirstPath(call) is { } path)
            approved = new Approved(id, Text(call, "kind"), path);
        lock (_gate) TurnOf(sessionId).Allowed = approved;
    }

    /// <summary>
    /// Reads a version 1 tool update as received and returns the update to use, or null to drop it. A new tool with no
    /// status is pending; pending or in_progress keeps it open; completed, failed or cancelled closes it.
    /// </summary>
    public SessionNotification? Read(SessionNotification received)
    {
        if (received.Kind is not ("tool_call" or "tool_call_update") || received.Update.ValueKind != JsonValueKind.Object
            || Text(received.Update, "toolCallId") is not { Length: > 0 } toolCallId) return received;
        var status = Text(received.Update, "status") ?? (received.Kind == "tool_call" ? "pending" : null);
        string? runOf;
        bool stopped = false;
        lock (_gate)
        {
            var turn = TurnOf(received.SessionId);
            if (received.Kind == "tool_call" && turn.Allowed is { } allowed)
            {
                turn.Allowed = null;
                if (allowed.ToolCallId != toolCallId && Text(received.Update, "kind") == allowed.Kind
                    && FirstPath(received.Update) == allowed.Path) turn.RunOf[toolCallId] = allowed.ToolCallId;
            }
            runOf = turn.RunOf.GetValueOrDefault(toolCallId);
            var id = runOf ?? toolCallId;
            if (runOf is null && turn.RanThrough.Contains(id)) return null;
            if (status is "pending" or "in_progress") turn.Open.Add(id);
            else if (status is not null)
            {
                stopped = turn.Open.Remove(id) && status == "failed" && turn.Stopping;
                if (runOf is not null) turn.RanThrough.Add(id);
            }
        }
        if (runOf is null && !stopped) return received;
        var update = JsonNode.Parse(received.Update.GetRawText())!.AsObject();
        if (runOf is not null)
        {
            // The run keeps A's row: A's id, title, kind, diff and location; only its progress and result come through.
            update["sessionUpdate"] = "tool_call_update";
            update["toolCallId"] = runOf;
            foreach (var own in new[] { "title", "kind", "content", "locations", "rawInput" }) update.Remove(own);
        }
        if (stopped) update["status"] = "cancelled";
        return received with { Update = UpdateNormalizer.Element(update) };
    }

    /// <summary>The updates that close the session's open tools at the end of its turn, which forgets the turn.</summary>
    public IReadOnlyList<SessionNotification> Close(string sessionId, string stopReason)
    {
        string[] open;
        lock (_gate)
        {
            if (!_turns.Remove(sessionId, out var turn)) return [];
            open = [.. turn.Open];
        }
        var status = stopReason == "cancelled" ? "cancelled" : "failed";
        return [.. open.Select(id => new SessionNotification(sessionId, JsonSerializer.SerializeToElement(
            new ToolClosed("tool_call_update", id, status), AcpJsonContext.Default.ToolClosed)))];
    }

    private Turn TurnOf(string sessionId)
    {
        if (!_turns.TryGetValue(sessionId, out var turn)) _turns[sessionId] = turn = new Turn();
        return turn;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstPath(JsonElement call) =>
        call.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
        && locations.GetArrayLength() > 0 && locations[0].ValueKind == JsonValueKind.Object
            ? Text(locations[0], "path") : null;
}

/// <summary>A version 1 tool the turn's end closed.</summary>
internal sealed record ToolClosed(string SessionUpdate, string ToolCallId, string Status);
