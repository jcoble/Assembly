using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Mcp.Tools;
using Assembly.Application.Common.Engine;

namespace Assembly.Engine.Mcp;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AskOwnerArguments))]
[JsonSerializable(typeof(AskOwnerAnswer))]
[JsonSerializable(typeof(ReportArguments))]
[JsonSerializable(typeof(ReportAnswer))]
[JsonSerializable(typeof(LinkTaskArguments))]
[JsonSerializable(typeof(LinkTaskAnswer))]
[JsonSerializable(typeof(StartSessionArguments))]
[JsonSerializable(typeof(StartSessionAnswer))]
[JsonSerializable(typeof(GetSessionTitleArguments))]
[JsonSerializable(typeof(SessionTitleStat))]
[JsonSerializable(typeof(SessionTitleChunk))]
[JsonSerializable(typeof(ListSessionsArguments))]
[JsonSerializable(typeof(ListSessionsAnswer))]
[JsonSerializable(typeof(SendMessageArguments))]
[JsonSerializable(typeof(SendMessageAnswer))]
[JsonSerializable(typeof(MessageStatusArguments))]
[JsonSerializable(typeof(MessageStatusAnswer))]
[JsonSerializable(typeof(WorkflowFormatAnswer))]
[JsonSerializable(typeof(CheckWorkflowArguments))]
[JsonSerializable(typeof(CheckWorkflowAnswer))]
[JsonSerializable(typeof(StartWorkflowArguments))]
[JsonSerializable(typeof(StartWorkflowAnswer))]
[JsonSerializable(typeof(McpCaller))]
[JsonSerializable(typeof(CallerRow))]
[JsonSerializable(typeof(McpRefusal))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class McpJson : JsonSerializerContext;
