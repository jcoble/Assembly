using System.Globalization;
using System.Text.Json;
using Assembly.Engine.Acp.JsonRpc;
using Assembly.Engine.Acp.Messages;
using Assembly.Engine.Events;

namespace Assembly.Engine.Acp.V1;

/// <summary>
/// ACP v1 to our own events. This is the only place that reads ACP's update shapes, so a v2 reader
/// can sit beside it without the converter changing.
/// </summary>
public static class UpdateReader
{
    /// <summary>The event one <c>session/update</c> carries, or null for the kinds we don't show.</summary>
    public static AgentEvent? Read(SessionNotification notification, long at)
    {
        var update = notification.Update;
        return notification.Kind switch
        {
            "agent_message_chunk" when ChunkText(update) is { } text => new TextChunk(at, String(update, "messageId"), text),
            "agent_thought_chunk" when ChunkText(update) is { } text => new ThoughtChunk(at, String(update, "messageId"), text),
            "tool_call" => ToolCall(update, at, started: true),
            "tool_call_update" => ToolCall(update, at, started: false),
            "plan" => Plan(update, at),
            "usage_update" => Usage(update, at),
            "session_info_update" when String(update, "title") is { Length: > 0 } title => new TitleChanged(at, title),
            "config_option_update" when update.TryGetProperty("configOptions", out var options) => new ConfigOptionsChanged(at, options.Clone()),
            "available_commands_update" when update.TryGetProperty("availableCommands", out var commands)
                && commands.ValueKind == JsonValueKind.Array => new CommandsChanged(at, commands.Clone()),
            "subagent_update" when String(update, "sessionId") is { Length: > 0 } child => Subagent(update, child, at),
            "session_message" => new SessionMessage(at, String(update, "messageId"), MessageText(update)),
            // current_mode_update,
            // user_message_chunk (a reload's replay) and anything newer aren't chat rows.
            _ => null,
        };
    }

    /// <summary>
    /// Every event one <c>session/update</c> carries: <see cref="Read"/>'s, background tasks
    /// (<c>async_task_spawned</c>, <c>async_task_state_update</c>), and what one agent's extensions say about its
    /// background work and when a stretch of it ends.
    /// </summary>
    public static IEnumerable<AgentEvent> ReadAll(SessionNotification notification, long at)
    {
        if (Read(notification, at) is { } main)
            yield return main;
        if (Task(notification.Kind, notification.Update, at) is { } task)
            yield return task;
        foreach (var extra in ClaudeBackground.Read(notification.Kind, notification.Update, at))
            yield return extra;
        foreach (var extra in CodexThreadStatus.Read(notification.Kind, notification.Update, at))
            yield return extra;
    }

    private static TaskChanged? Task(string kind, JsonElement update, long at)
    {
        if (kind is not ("async_task_spawned" or "async_task_state_update") || String(update, "asyncTaskId") is not { } id)
            return null;
        var state = kind == "async_task_spawned" ? "running" : String(update, "state") switch
        {
            "running" or "completed" or "failed" => String(update, "state")!,
            "stopped" or "cancelled" or "killed" => "stopped",
            _ => "failed",
        };
        var canStop = update.TryGetProperty("canStop", out var stop) && stop.ValueKind == JsonValueKind.True;
        return new TaskChanged(at, id, String(update, "name") ?? "", state, canStop, String(update, "toolCallId"));
    }

    /// <summary>The prompt's result, as the turn's end.</summary>
    public static TurnEnded TurnEnd(PromptResponse response, long at) =>
        new(at, response.StopReason, response.Usage is { } usage
            ? new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CachedReadTokens, usage.CachedWriteTokens)
            : null);

    /// <summary>The prompt's error, as the turn's failure.</summary>
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
            ContentText: ContentText(update),
            Diffs: Diffs(update),
            Paths: Paths(update));
        return new ToolCallChanged(at, id, started, fields);
    }

    private static PlanChanged? Plan(JsonElement update, long at)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return null;
        var steps = entries.EnumerateArray()
            .Select(entry => new PlanEntry(String(entry, "content") ?? "", String(entry, "status") ?? "pending"))
            .ToList();
        return new PlanChanged(at, steps);
    }

    private static SubagentChanged Subagent(JsonElement update, string child, long at)
    {
        string? state = null, stopReason = null, error = null;
        if (update.TryGetProperty("state", out var work) && work.ValueKind == JsonValueKind.Object)
            (state, stopReason, error) = (String(work, "state"), String(work, "stopReason"), String(work, "error"));
        bool? canCancel = update.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
            ? capabilities.TryGetProperty("cancel", out _)
            : null;
        return new SubagentChanged(at, child, String(update, "title"), String(update, "description"), state, stopReason, error, canCancel);
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

    private static string? ContentText(JsonElement update)
    {
        var texts = ContentItems(update)
            .Where(item => String(item, "type") == "content" && item.TryGetProperty("content", out var inner) && String(inner, "type") == "text")
            .Select(item => String(item.GetProperty("content"), "text"))
            .OfType<string>()
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    private static IReadOnlyList<FileDiff>? Diffs(JsonElement update)
    {
        var diffs = ContentItems(update)
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
        update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
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
