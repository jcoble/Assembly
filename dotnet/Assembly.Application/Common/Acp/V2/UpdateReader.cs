using System.Globalization;
using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Domain.Events;

namespace Assembly.Application.Common.Acp.V2;

/// <summary>
/// ACP v2 upserts, chunks and foreground snapshots to domain events.
/// </summary>
public sealed class UpdateReader
{
    private readonly Dictionary<string, ToolCallFields> _tools = [];
    private readonly Dictionary<string, List<JsonElement>> _toolContent = [];
    private readonly Dictionary<string, TaskChanged> _background = [];
    private readonly Dictionary<string, IReadOnlyList<PlanEntry>> _plans = [];
    private readonly Dictionary<string, SubagentChanged> _subagents = [];
    private readonly Dictionary<string, ProviderSessionMessage> _providerMessages = [];
    private bool _running;
    private bool _idle;
    private string? _lastStop;

    /// <summary>Applies updates in arrival order; one reader belongs to one provider session.</summary>
    public IReadOnlyList<AgentEvent> Read(SessionNotification notification, long? timestamp = null)
    {
        var at = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var update = notification.Update;
        List<AgentEvent> events = [];
        switch (notification.Kind)
        {
            case "agent_message_chunk":
            case "agent_thought_chunk":
                if (ChunkText(update) is { } chunk)
                    events.Add(notification.Kind == "agent_message_chunk"
                        ? new TextChunk(at, String(update, "messageId"), chunk)
                        : new ThoughtChunk(at, String(update, "messageId"), chunk));
                break;
            case "agent_message":
            case "agent_thought":
                if (update.TryGetProperty("content", out _))
                    events.Add(notification.Kind == "agent_message"
                        ? new TextChunk(at, String(update, "messageId"), MessageText(update), Replace: true)
                        : new ThoughtChunk(at, String(update, "messageId"), MessageText(update), Replace: true));
                break;
            case "user_message":
            case "user_message_chunk":
                events.Add(new SessionMessage(at, String(update, "messageId"),
                    notification.Kind == "user_message" ? MessageText(update) : ChunkText(update) ?? ""));
                break;
            case "session_message":
            case "session_message_chunk":
                if (String(update, "messageId") is not { } messageId) break;
                _providerMessages.TryGetValue(messageId, out var message);
                var blocks = message?.Content ?? [];
                if (update.TryGetProperty("content", out var messageContent))
                    blocks = notification.Kind == "session_message"
                        ? Array.AsReadOnly(messageContent.ValueKind == JsonValueKind.Array
                            ? messageContent.EnumerateArray().Select(block => block.Clone()).ToArray() : [])
                        : Array.AsReadOnly(blocks.Append(messageContent.Clone()).ToArray());
                string? Participant(string field, string? prior) => notification.Kind == "session_message"
                    && update.TryGetProperty(field, out _) ? String(update, field) : prior;
                var observed = new ProviderSessionMessage(at, notification.SessionId, messageId,
                    Participant("senderSessionId", message?.SenderSessionId),
                    Participant("recipientSessionId", message?.RecipientSessionId),
                    Participant("senderMessageId", message?.SenderMessageId),
                    Participant("recipientMessageId", message?.RecipientMessageId), blocks);
                _providerMessages[messageId] = observed;
                events.Add(observed);
                break;
            case "tool_call_update":
            case "tool_call_content_chunk":
                if (String(update, "toolCallId") is not { } id) break;
                var started = !_tools.TryGetValue(id, out var previous);
                if (!_toolContent.TryGetValue(id, out var toolContent)) _toolContent[id] = toolContent = [];
                if (update.TryGetProperty("content", out _))
                {
                    if (notification.Kind == "tool_call_update") toolContent.Clear();
                    toolContent.AddRange(ContentItems(update).Select(item => item.Clone()));
                }
                string? Patch(string key, string? prior) => update.TryGetProperty(key, out _) ? String(update, key) : prior;
                var fields = new ToolCallFields(
                    update.TryGetProperty("name", out _) ? String(update, "name") : ClaudeToolName(update) ?? previous?.Name,
                    Patch("kind", previous?.Kind) ?? "other", Patch("title", previous?.Title),
                    Patch("status", previous?.Status) ?? "pending",
                    update.TryGetProperty("rawInput", out var input) ? input.ValueKind == JsonValueKind.Null ? null : input.Clone() : previous?.Input,
                    update.TryGetProperty("rawOutput", out var output) ? AsText(output) : previous?.Output,
                    ContentText(toolContent), Diffs(toolContent),
                    update.TryGetProperty("locations", out _) ? Paths(update) : previous?.Paths, ContentReplaced: true,
                    TerminalIds: Array.AsReadOnly(toolContent.Where(item => String(item, "type") == "terminal")
                        .Select(item => String(item, "terminalId")).OfType<string>().Distinct().ToArray()),
                    AcpSessionId: notification.SessionId, Snapshot: true);
                _tools[id] = fields;
                events.Add(new ToolCallChanged(at, id, started, fields));
                if (update.TryGetProperty("status", out _) && fields.Status is "completed" or "failed" or "cancelled")
                {
                    if (_background.Remove(id, out var work))
                        events.Add(work with { At = at, State = fields.Status == "cancelled" ? "stopped" : fields.Status });
                }
                else if (_idle && fields.Status == "in_progress")
                    events.AddRange(RetainOutstanding(at));
                break;
            case "terminal_update":
            case "terminal_output_chunk":
                if (String(update, "terminalId") is not { } terminalId) break;
                var replaced = notification.Kind == "terminal_update" && update.TryGetProperty("output", out _);
                var data = notification.Kind == "terminal_output_chunk" ? String(update, "data")
                    : update.TryGetProperty("output", out var terminalOutput) ? String(terminalOutput, "data") : null;
                var exitReplaced = update.TryGetProperty("exitStatus", out var exit);
                events.Add(new TerminalChanged(at, terminalId, data, replaced,
                    update.TryGetProperty("command", out _), String(update, "command"),
                    update.TryGetProperty("cwd", out _), String(update, "cwd"),
                    exitReplaced, exit.ValueKind == JsonValueKind.Object ? new TerminalExit(Long(exit, "exitCode"), String(exit, "signal")) : null,
                    notification.SessionId));
                break;
            case "subagent_update":
                if (String(update, "sessionId") is not { } childId) break;
                _subagents.TryGetValue(childId, out var child);
                var childTitle = update.TryGetProperty("title", out _) ? String(update, "title") : child?.Title;
                var description = update.TryGetProperty("description", out _) ? String(update, "description") : child?.Description;
                var canCancel = child?.CanCancel ?? false;
                if (update.TryGetProperty("capabilities", out var capabilities))
                    canCancel = capabilities.ValueKind == JsonValueKind.Object
                        && capabilities.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.Object;
                var childState = child?.State ?? "unknown";
                var stop = child?.StopReason;
                var error = child?.Error;
                if (update.TryGetProperty("state", out var snapshot))
                {
                    childState = String(snapshot, "state") ?? "unknown";
                    stop = String(snapshot, "stopReason");
                    error = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("error", out var failure)
                        ? String(failure, "message") : null;
                }
                var changed = new SubagentChanged(at, childId, childTitle, description, childState, stop, error, canCancel);
                _subagents[childId] = changed;
                events.Add(changed);
                break;
            case "plan_update":
                if (Plan(update, at) is { } plan && update.TryGetProperty("plan", out var content)
                    && String(content, "planId") is { } planId)
                {
                    _plans[planId] = plan.Entries;
                    events.Add(new PlanChanged(at, _plans.Values.SelectMany(entries => entries).ToList()));
                }
                break;
            case "state_update":
                if (update.Deserialize(AcpJsonContext.Default.StateUpdate) is not { } state) break;
                if (state.State is "running" or "requires_action")
                {
                    _idle = false;
                    if (!_running) events.Add(new TurnStarted(at, Guid.NewGuid().ToString(), ""));
                    _running = true;
                    _lastStop = null;
                }
                else if (state.State == "idle")
                {
                    _idle = true;
                    events.AddRange(RetainOutstanding(at));
                    var shouldEnd = _running || state.StopReason != _lastStop;
                    _running = false;
                    _lastStop = state.StopReason;
                    if (!shouldEnd) break;
                    if (state.StopReason == "error")
                        events.Add(new TurnFailed(at, state.Error?.Message ?? "The agent failed.", state.Error?.Code.ToString(CultureInfo.InvariantCulture)));
                    else if (state.StopReason is { } reason)
                        events.Add(new TurnEnded(at, reason, state.ReportedUsage is { } usage
                            ? new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CachedReadTokens, usage.CachedWriteTokens) : null));
                    if (state.ReportedUsage is { } report)
                        events.Add(new UsageReported(at, report.CostUsd, report.Model, report.Used, report.Size));
                }
                else if (state.State == "unknown")
                {
                    _running = false;
                    _idle = false;
                }
                break;
            case "usage_update": // Also emitted by both adapters at protocol 2.
                events.Add(Usage(update, at));
                break;
            case "session_info_update":
                if (String(update, "title") is { Length: > 0 } title) events.Add(new TitleChanged(at, title));
                break;
            case "config_option_update":
                if (update.TryGetProperty("configOptions", out var options)) events.Add(new ConfigOptionsChanged(at, options.Clone()));
                break;
            case "available_commands_update":
                if (update.TryGetProperty("availableCommands", out var commands)) events.Add(new CommandsChanged(at, commands.Clone()));
                break;
        }
        return events;
    }

    public static TurnEnded TurnEnd(PromptResult response, long at) =>
        new(at, response.StopReason, response.Usage is { } usage
            ? new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CachedReadTokens, usage.CachedWriteTokens) : null);

    public static TurnFailed TurnFailure(JsonRpcException error, long at) =>
        new(at, error.Message, error.Code.ToString(CultureInfo.InvariantCulture));

    internal IReadOnlyList<TaskChanged> RetainOutstanding(long at)
    {
        List<TaskChanged> retained = [];
        foreach (var (id, fields) in _tools)
        {
            if (fields.Status != "in_progress" || _background.ContainsKey(id)) continue;
            var task = new TaskChanged(at, id, fields.Name ?? fields.Title ?? "", "running", false, id, Tracked: true);
            _background[id] = task;
            retained.Add(task);
        }
        return retained;
    }

    private static PlanChanged? Plan(JsonElement update, long at)
    {
        if (!update.TryGetProperty("plan", out var plan) || String(plan, "type") != "items" || !plan.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return null;
        var steps = entries.EnumerateArray()
            .Select(entry => new PlanEntry(String(entry, "content") ?? "", String(entry, "status") ?? "pending"))
            .ToList();
        return new PlanChanged(at, steps);
    }

    // A message's text blocks, joined.
    private static string MessageText(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n\n", content.EnumerateArray()
                .Where(block => String(block, "type") == "text").Select(block => String(block, "text") ?? ""))
            : "";

    private static UsageReported Usage(JsonElement update, long at)
    {
        decimal? cost = null;
        if (update.TryGetProperty("cost", out var amount) && amount.ValueKind == JsonValueKind.Object
            && String(amount, "currency") == "USD" && amount.TryGetProperty("amount", out var value) && value.TryGetDouble(out var usd))
            // The agent sums costs as doubles (0.15624559999999998); a double's 15 digits as a decimal drop the noise.
            cost = (decimal)usd;
        string? model = null;
        if (update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            model = String(meta, "_claude/model");
        return new UsageReported(at, cost, model, Long(update, "used"), Long(update, "size"));
    }

    private static string? ChunkText(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && String(content, "type") == "text"
            ? String(content, "text")
            : null;

    /// <summary>Claude's adapter names the tool in <c>_meta.claudeCode.toolName</c>; ACP itself only has a title.</summary>
    private static string? ClaudeToolName(JsonElement update) =>
        update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty("claudeCode", out var claude) && claude.ValueKind == JsonValueKind.Object
            ? String(claude, "toolName")
            : null;

    private static string? ContentText(IEnumerable<JsonElement> content)
    {
        var texts = content
            .Where(item => String(item, "type") == "content" && item.TryGetProperty("content", out var inner) && String(inner, "type") == "text")
            .Select(item => String(item.GetProperty("content"), "text"))
            .OfType<string>()
            .ToList();
        return texts.Count == 0 ? null : string.Concat(texts);
    }

    private static IReadOnlyList<FileDiff>? Diffs(IEnumerable<JsonElement> content)
    {
        var diffs = content
            .Where(item => String(item, "type") == "diff" && String(item, "path") is not null && String(item, "newText") is not null)
            .Select(item => new FileDiff(String(item, "path")!, String(item, "oldText"), String(item, "newText")!))
            .ToList();
        return diffs.Count == 0 ? null : Array.AsReadOnly(diffs.ToArray());
    }

    private static IReadOnlyList<string>? Paths(JsonElement update)
    {
        if (!update.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            return null;
        var paths = locations.EnumerateArray().Select(location => String(location, "path")).OfType<string>().ToList();
        return paths.Count == 0 ? null : Array.AsReadOnly(paths.ToArray());
    }

    private static IEnumerable<JsonElement> ContentItems(JsonElement update) =>
        update.TryGetProperty("content", out var content)
            ? content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                : content.ValueKind == JsonValueKind.Object ? [content] : []
            : [];

    private static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    private static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
