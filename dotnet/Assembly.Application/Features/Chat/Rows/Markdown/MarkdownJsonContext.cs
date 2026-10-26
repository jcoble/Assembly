using System.Text.Json.Serialization;

using Assembly.Domain.Markdown;
namespace Assembly.Application.Features.Chat.Rows.Markdown;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<SafeBlock>))]
[JsonSerializable(typeof(SafeBlock))]
internal sealed partial class MarkdownJsonContext : JsonSerializerContext;
