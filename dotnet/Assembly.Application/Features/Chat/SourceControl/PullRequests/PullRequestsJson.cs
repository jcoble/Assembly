using System.Text.Json.Serialization;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PullRequestPage))]
[JsonSerializable(typeof(PullRequestDetail))]
[JsonSerializable(typeof(PrFileContext))]
[JsonSerializable(typeof(PullRequestWriter.ReviewPayload))]
[JsonSerializable(typeof(PullRequestWriter.ReviewCommentPayload))]
[JsonSerializable(typeof(PullRequestWriter.BodyPayload))]
[JsonSerializable(typeof(PullRequestWriter.MergePayload))]
[JsonSerializable(typeof(PullRequestWriter.UpdateBranchPayload))]
internal sealed partial class PullRequestsJson : JsonSerializerContext;
