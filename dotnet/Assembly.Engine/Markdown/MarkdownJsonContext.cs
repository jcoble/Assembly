using System.Text.Json.Serialization;

namespace Assembly.Engine.Markdown;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<SafeBlock>))]
[JsonSerializable(typeof(SafeBlock))]
internal sealed partial class MarkdownJsonContext : JsonSerializerContext;
