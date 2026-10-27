using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Chat.Sessions;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionItem))]
[JsonSerializable(typeof(SessionsFeature.PageArgs))]
[JsonSerializable(typeof(SessionsFeature.Page))]
[JsonSerializable(typeof(SessionsFeature.CreateArgs))]
[JsonSerializable(typeof(SessionsFeature.Created))]
[JsonSerializable(typeof(SessionsFeature.ReadArgs))]
[JsonSerializable(typeof(SendFeature.SendArgs))]
[JsonSerializable(typeof(SendFeature.Sent))]
[JsonSerializable(typeof(SendFeature.StopArgs))]
[JsonSerializable(typeof(SendFeature.AnswerArgs))]
[JsonSerializable(typeof(SendFeature.StopTaskArgs))]
[JsonSerializable(typeof(SendFeature.Done))]
[JsonSerializable(typeof(ConfigFeature.GetArgs))]
[JsonSerializable(typeof(ConfigFeature.SetArgs))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MachinesFeature.ListArgs))]
[JsonSerializable(typeof(MachinesFeature.MachineList))]
[JsonSerializable(typeof(MachinesFeature.AgentsArgs))]
[JsonSerializable(typeof(MachinesFeature.AgentList))]
internal sealed partial class SessionsJson : JsonSerializerContext;
