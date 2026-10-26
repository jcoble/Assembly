using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// A <c>session/update</c>. <c>Update</c> stays raw here; its <c>sessionUpdate</c> field says which kind it is
/// (<c>agent_message_chunk</c>, <c>tool_call</c>, <c>plan</c>, <c>usage_update</c>…). Step 2's converter turns these into rows.
/// </summary>
public sealed record SessionNotification(string SessionId, JsonElement Update)
{
    [JsonIgnore]
    public string Kind =>
        Update.ValueKind == JsonValueKind.Object && Update.TryGetProperty("sessionUpdate", out var kind)
            ? kind.GetString() ?? ""
            : "";
}
