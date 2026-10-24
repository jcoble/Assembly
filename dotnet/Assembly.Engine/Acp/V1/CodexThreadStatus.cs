using System.Text.Json;
using Assembly.Engine.Events;

namespace Assembly.Engine.Acp.V1;

/// <summary>Codex's thread going idle (<c>session_info_update._meta.codex.threadStatus</c>) ends its stretch of work.</summary>
public static class CodexThreadStatus
{
    public static IEnumerable<AgentEvent> Read(string kind, JsonElement update, long at)
    {
        if (kind == "session_info_update" && update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object
            && codex.TryGetProperty("threadStatus", out var status) && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("type", out var type) && type.GetString() is "idle" or "systemError")
            yield return new CycleEnded(at, null);
    }
}
