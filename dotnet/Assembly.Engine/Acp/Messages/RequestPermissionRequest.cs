using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Acp.Messages;

public sealed record RequestPermissionRequest(string SessionId, JsonElement ToolCall, IReadOnlyList<PermissionOption> Options)
{
    [JsonIgnore]
    public string ToolTitle =>
        ToolCall.ValueKind == JsonValueKind.Object && ToolCall.TryGetProperty("title", out var title)
            ? title.GetString() ?? ""
            : "";
}
