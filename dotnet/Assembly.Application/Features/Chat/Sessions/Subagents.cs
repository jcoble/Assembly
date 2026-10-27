using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V1;
using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's subagents that the agent runs as child sessions (the subagents RFD). Each child has a session of its
/// own, with its own rows: each prompt its parent gives it starts a run there, and the parent saying it's idle ends
/// that run. The parent's session gets a row for each child, which <see cref="SessionHost"/> applies. Used under the
/// host's lock.
/// </summary>
internal sealed class Subagents(SessionStore sessionStore, LiveHub hub)
{
    private sealed class Child(SessionInfo session, SubagentPayload shown)
    {
        public SessionInfo Session { get; } = session;
        public RowConverter Converter { get; } = new();
        public SubagentPayload Shown { get; set; } = shown;
        public string? RunId { get; set; }
        // The call the child hands its report back with, whose own updates aren't shown.
        public string? Handback { get; set; }
    }

    private readonly Dictionary<string, Child> _children = [];

    public bool Has(string acpSessionId) => _children.ContainsKey(acpSessionId);

    /// <summary>The child's title as its parent's row shows it, or null for a session that isn't one of its children.</summary>
    public string? Title(string acpSessionId) => _children.TryGetValue(acpSessionId, out var child) ? child.Shown.Title : null;

    /// <summary>Children still at work: they keep their parent's session open.</summary>
    public bool AnyRunning => _children.Values.Any(child => child.Shown.State != "idle");

    /// <summary>The child sessions that can be stopped now.</summary>
    public IEnumerable<string> Stoppable => _children.Where(pair => pair.Value.Shown.CanStop).Select(pair => pair.Key);

    /// <summary>The parent's word on a child: its row in the parent's session, as it stands now.</summary>
    public SubagentShown Changed(SessionInfo parent, SubagentChanged change)
    {
        if (!_children.TryGetValue(change.AcpSessionId, out var child))
        {
            var title = change.Title ?? change.Description ?? "Subagent";
            var session = sessionStore.Child(parent, change.AcpSessionId, title);
            child = _children[change.AcpSessionId] = new Child(session, new SubagentPayload(session.SessionId, title, change.Description,
                "running", null, null, false, null));
        }
        return Update(child, change);
    }

    private SubagentShown Update(Child child, SubagentChanged change)
    {
        var state = change.State ?? child.Shown.State;
        child.Shown = child.Shown with
        {
            Title = change.Title ?? child.Shown.Title,
            Description = change.Description ?? child.Shown.Description,
            State = state,
            StopReason = change.StopReason,
            Error = change.Error,
            CanStop = state != "idle" && (change.CanCancel ?? child.Shown.CanStop),
        };
        if (state == "idle")
            EndRun(child, change.At, change.Error is { } error ? new TurnFailed(change.At, error, "subagent_error")
                : new TurnEnded(change.At, change.StopReason == "cancelled" ? "cancelled" : "end_turn", null));
        return new SubagentShown(change.At, change.AcpSessionId, child.Shown);
    }

    /// <summary>
    /// One of a child's own updates, into its session. A prompt from the parent starts a run; when it names one of the
    /// parent's tool calls (<paramref name="parentKnowsTool"/>), the parent's row is nested under it, and comes back.
    /// </summary>
    public SubagentShown? Apply(SessionNotification update, long at, Func<string, bool> parentKnowsTool)
    {
        var child = _children[update.SessionId];
        SubagentShown? linked = null;
        foreach (var agentEvent in UpdateReader.ReadAll(update, at))
        {
            if (agentEvent is SessionMessage message)
            {
                EndRun(child, at, new TurnEnded(at, "end_turn", null));
                child.RunId = Guid.NewGuid().ToString();
                Publish(child, new TurnStarted(at, child.RunId, message.Text));
                if (child.Shown.ToolCallId is null && message.MessageId is { } id && parentKnowsTool(id))
                {
                    child.Shown = child.Shown with { ToolCallId = id };
                    linked = new SubagentShown(at, update.SessionId, child.Shown);
                }
                continue;
            }

            // Claude Code's subagent hands its report back as a tool call's input; the report is the child's reply.
            if (agentEvent is ToolCallChanged { Started: true, Fields.Name: "SubagentHandback" } handback
                && handback.Fields.Input is { ValueKind: JsonValueKind.Object } input
                && input.TryGetProperty("message", out var report) && report.ValueKind == JsonValueKind.String)
            {
                child.Handback = handback.ToolCallId;
                Publish(child, new TextChunk(at, $"handback:{handback.ToolCallId}", report.GetString()!));
                continue;
            }
            if (agentEvent is ToolCallChanged call && call.ToolCallId == child.Handback)
                continue;

            if (child.RunId is null)
            {
                child.RunId = Guid.NewGuid().ToString();
                Publish(child, new RunStarted(at, child.RunId, "Started by its parent"));
            }
            Publish(child, agentEvent);
        }
        return linked;
    }

    /// <summary>The parent's session closed: each child still at work stopped with it.</summary>
    public List<SubagentShown> StopAll(long at) =>
        _children.Where(pair => pair.Value.Shown.State != "idle").ToList()
            .Select(pair => Update(pair.Value, new SubagentChanged(at, pair.Key, null, null, "idle", "cancelled", null, false)))
            .ToList();

    private void EndRun(Child child, long at, AgentEvent end)
    {
        if (child.RunId is null)
            return;
        Publish(child, end);
        child.RunId = null;
    }

    private void Publish(Child child, AgentEvent agentEvent) => hub.Publish(child.Session.SessionId, child.Converter.Apply(agentEvent));
}
