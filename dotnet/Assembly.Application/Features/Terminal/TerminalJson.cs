using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Terminal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TerminalInfo))]
[JsonSerializable(typeof(StartArgs))]
[JsonSerializable(typeof(WriteArgs))]
[JsonSerializable(typeof(ResizeArgs))]
[JsonSerializable(typeof(TerminalKey))]
[JsonSerializable(typeof(ReadArgs))]
[JsonSerializable(typeof(OutputPiece))]
[JsonSerializable(typeof(Empty))]
internal sealed partial class TerminalJson : JsonSerializerContext;
