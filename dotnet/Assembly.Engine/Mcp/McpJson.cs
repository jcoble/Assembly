using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Mcp.Tools;

namespace Assembly.Engine.Mcp;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AskOwnerArguments))]
[JsonSerializable(typeof(AskOwnerAnswer))]
[JsonSerializable(typeof(StartSessionArguments))]
[JsonSerializable(typeof(StartSessionAnswer))]
[JsonSerializable(typeof(ListSessionsArguments))]
[JsonSerializable(typeof(ListSessionsAnswer))]
[JsonSerializable(typeof(McpCaller))]
[JsonSerializable(typeof(CallerRow))]
[JsonSerializable(typeof(McpRefusal))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class McpJson : JsonSerializerContext;
