using System.Text.Json;

namespace Assembly.Application.Common.Acp.JsonRpc;

/// <summary>The actual inbound envelope identity, scoped to one connection lifetime.</summary>
public sealed record JsonRpcRequestContext(string ConnectionId, JsonElement Id, Guid? OccurrenceId = null)
{
    public string ProviderRequestId => ConnectionId + (OccurrenceId is { } occurrence ? "/" + occurrence.ToString("N") : "") + ":" + Id.GetRawText();
}
