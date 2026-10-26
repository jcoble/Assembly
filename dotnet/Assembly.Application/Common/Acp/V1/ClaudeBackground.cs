using System.Text.Json;
using System.Text.RegularExpressions;
using Assembly.Domain.Events;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// Claude Code's background work, from what its adapter sends any client. The adapter's own task updates
/// (<c>async_task_*</c>) come only with the AIR asyncTasks opt-in, and any AIR opt-in makes claude-agent-acp treat us
/// as JetBrains AIR (<c>isAirClient</c>), which takes its subagents off the standard RFD child sessions. So a
/// backgrounded Bash call's result (or one moved to the background by a steer) starts an untracked task, and the end of the cycle Claude starts when that work
/// is done (its <c>usage_update</c> tagged <c>_claude/origin: task-notification</c>) ends them. The tag names no task.
/// </summary>
public static partial class ClaudeBackground
{
    public static IEnumerable<AgentEvent> Read(string kind, JsonElement update, long at)
    {
        if (kind == "tool_call_update" && update.TryGetProperty("rawOutput", out var output) && output.ValueKind == JsonValueKind.String
            && Backgrounded().Match(output.GetString()!) is { Success: true } match && update.TryGetProperty("toolCallId", out var call))
            yield return new TaskChanged(at, match.Groups[1].Value, "Background command", "running", CanStop: false, call.GetString(), Tracked: false);

        if (kind == "usage_update" && update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("_claude/origin", out var origin) && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out var name) && name.ValueKind == JsonValueKind.String)
            yield return new CycleEnded(at, name.GetString(), EndsUntracked: name.GetString() == "task-notification");
    }

    // Started in the background, or moved there so a message steered in could reach the agent.
    [GeneratedRegex(@"^Command (?:running in background with ID: |was moved to the background \(ID: )(\w+)")]
    private static partial Regex Backgrounded();
}
