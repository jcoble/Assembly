using System.Text.Json;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// Which ACP version an agent speaks, from the shape of its initialize reply. The number alone is not trusted: an agent
/// may echo the version it was asked for while sending version 1 fields. Version 1 names its fields
/// <c>agentInfo</c> and <c>agentCapabilities</c>; version 2 names them <c>info</c> and <c>capabilities</c>.
/// </summary>
internal static class ProtocolShape
{
    public const int Version1 = 1;

    public static int Of(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object
        && (reply.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number) && number == Version1
            || reply.TryGetProperty("agentCapabilities", out _)
            || reply.TryGetProperty("agentInfo", out _))
            ? Version1
            : AcpProtocol.Version;
}
