using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Git;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitOperationHost))]
[JsonSerializable(typeof(GitOperationStatus))]
[JsonSerializable(typeof(GitOperationStart))]
[JsonSerializable(typeof(GitOperationHostArgs))]
[JsonSerializable(typeof(GitOperationArgs))]
internal sealed partial class GitOperationsJson : JsonSerializerContext;
