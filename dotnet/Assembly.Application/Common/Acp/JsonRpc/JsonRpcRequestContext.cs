using System.Text.Json;

namespace Assembly.Application.Common.Acp.JsonRpc;

/// <summary>The actual inbound envelope identity, scoped to one connection lifetime.</summary>
public sealed record JsonRpcRequestContext(string ConnectionId, JsonElement Id)
{
    public string ProviderRequestId => ConnectionId + ":" + Id.GetRawText();
}
