using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

public sealed record RequestPermissionRequest(string SessionId, string Title, string? Description,
    PermissionSubject? Subject, IReadOnlyList<PermissionOption> Options)
{
    [JsonIgnore]
    public string ToolTitle => Title;

    [JsonIgnore]
    public JsonElement ToolCall => Subject?.ToolCall ?? default;
}
