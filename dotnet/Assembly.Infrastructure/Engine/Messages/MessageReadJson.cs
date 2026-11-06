using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Messages.Reading;

namespace Assembly.Infrastructure.Engine.Messages;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(MessageDisplay[]))]
[JsonSerializable(typeof(MessageOrderInput[]))]
[JsonSerializable(typeof(SenderOrderArgs))]
[JsonSerializable(typeof(SenderOrderPage))]
[JsonSerializable(typeof(SenderOrderFact))]
[JsonSerializable(typeof(ReceiverOrderArgs))]
[JsonSerializable(typeof(ReceiverOrderPage))]
[JsonSerializable(typeof(ReceiverOrderFact))]
[JsonSerializable(typeof(MessagesPageArgs))]
[JsonSerializable(typeof(MessagesCountArgs))]
[JsonSerializable(typeof(BodyArgs))]
[JsonSerializable(typeof(MatchArgs))]
[JsonSerializable(typeof(MessagePage))]
[JsonSerializable(typeof(MessageCount))]
[JsonSerializable(typeof(MessageBody))]
[JsonSerializable(typeof(BodyMatch))]
[JsonSerializable(typeof(JsonElement))]
internal partial class MessageReadJson : JsonSerializerContext;
