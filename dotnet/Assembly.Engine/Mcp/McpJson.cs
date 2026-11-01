using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Mcp;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(McpCaller))]
[JsonSerializable(typeof(CallerRow))]
[JsonSerializable(typeof(McpRefusal))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class McpJson : JsonSerializerContext;
