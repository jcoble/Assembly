using System.Text.Json.Serialization;
namespace Assembly.Application.Features.Chat.SourceControl;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitHistoryPage))]
internal sealed partial class GitHistoryJson : JsonSerializerContext;
