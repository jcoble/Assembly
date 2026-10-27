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

    /// <summary>
    /// Never on the wire: the client queues it, in pipe order, as the agent's answer to <c>session/resume</c> arrives,
    /// so every update before it in the queue came before the answer. Only <c>UpdateRouter</c> reads it.
    /// </summary>
    internal const string ResumedKind = "_resumed";

    internal static SessionNotification Resumed(string sessionId) =>
        new(sessionId, JsonDocument.Parse($"{{\"sessionUpdate\":\"{ResumedKind}\"}}").RootElement.Clone());
}
