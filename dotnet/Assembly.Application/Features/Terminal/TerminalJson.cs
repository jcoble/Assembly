using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Terminal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TerminalInfo))]
[JsonSerializable(typeof(IReadOnlyList<TerminalInfo>))]
[JsonSerializable(typeof(StartArgs))]
[JsonSerializable(typeof(WriteArgs))]
[JsonSerializable(typeof(ResizeArgs))]
[JsonSerializable(typeof(TerminalKey))]
[JsonSerializable(typeof(ReadArgs))]
[JsonSerializable(typeof(OutputPiece))]
[JsonSerializable(typeof(OutputRow))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(ListArgs))]
internal sealed partial class TerminalJson : JsonSerializerContext;
