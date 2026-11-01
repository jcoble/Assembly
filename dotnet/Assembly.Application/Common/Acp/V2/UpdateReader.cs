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
                var patch = ToolCall(update, at, started)!.Fields;
                if (!_toolContent.TryGetValue(id, out var toolContent)) _toolContent[id] = toolContent = [];
                if (update.TryGetProperty("content", out _))
                {
                    if (notification.Kind == "tool_call_update") toolContent.Clear();
                    toolContent.AddRange(ContentItems(update).Select(item => item.Clone()));
                }
                var fields = new ToolCallFields(
                    patch.Name ?? previous?.Name, patch.Kind ?? previous?.Kind, patch.Title ?? previous?.Title,
                    patch.Status ?? previous?.Status, patch.Input ?? previous?.Input, patch.Output ?? previous?.Output,
                    ContentText(toolContent), Diffs(toolContent), patch.Paths ?? previous?.Paths, ContentReplaced: true);
                _tools[id] = fields;
                events.Add(new ToolCallChanged(at, id, started, fields));
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
                    if (!_running) events.Add(new TurnStarted(at, Guid.NewGuid().ToString(), ""));
                    _running = true;
                    _lastStop = null;
                }
                else if (state.State == "idle" && (_running || state.StopReason != _lastStop))
                {
                    _running = false;
                    _lastStop = state.StopReason;
                    if (state.StopReason == "error")
                        events.Add(new TurnFailed(at, state.Error?.Message ?? "The agent failed.", state.Error?.Code.ToString(CultureInfo.InvariantCulture)));
                    else if (state.StopReason is { } reason)
                        events.Add(new TurnEnded(at, reason, state.ReportedUsage is { } usage
                            ? new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CachedReadTokens, usage.CachedWriteTokens) : null));
                    if (state.ReportedUsage is { } report)
                        events.Add(new UsageReported(at, report.CostUsd, report.Model, report.Used, report.Size));
                }
                if (state.Background is not null)
                {
                    var present = state.Background.Select(work => work.Id).ToHashSet();
                    foreach (var ended in _background.Values.Where(work => !present.Contains(work.TaskId)).ToArray())
                    {
                        events.Add(ended with { At = at, State = "completed" });
                        _background.Remove(ended.TaskId);
                    }
                    foreach (var work in state.Background)
                    {
                        var task = new TaskChanged(at, work.Id, work.Title ?? "", work.State ?? "running", false, work.ToolCallId);
                        if (!_background.TryGetValue(work.Id, out var known) || known with { At = at } != task) events.Add(task);
                        if (task.Running) _background[work.Id] = task;
                        else _background.Remove(work.Id);
                    }
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

    private static ToolCallChanged? ToolCall(JsonElement update, long at, bool started)
    {
        if (String(update, "toolCallId") is not { } id)
            return null;

        var fields = new ToolCallFields(
            Name: String(update, "name") ?? ClaudeToolName(update),
            Kind: String(update, "kind"),
            Title: String(update, "title"),
            Status: String(update, "status"),
            Input: update.TryGetProperty("rawInput", out var input) && input.ValueKind == JsonValueKind.Object ? input.Clone() : null,
            Output: update.TryGetProperty("rawOutput", out var output) ? AsText(output) : null,
            Paths: Paths(update));
        return new ToolCallChanged(at, id, started, fields);
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
        return diffs.Count == 0 ? null : diffs;
    }

    private static IReadOnlyList<string>? Paths(JsonElement update)
    {
        if (!update.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            return null;
        var paths = locations.EnumerateArray().Select(location => String(location, "path")).OfType<string>().ToList();
        return paths.Count == 0 ? null : paths;
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
