using System.Text.Json.Serialization;
using Assembly.Application.Features.Workflows.Contracts;
namespace Assembly.Infrastructure.Workflows;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(BoardTask))]
[JsonSerializable(typeof(BoardQuestion))]
[JsonSerializable(typeof(BoardEvent))]
[JsonSerializable(typeof(BoardMetrics))]
[JsonSerializable(typeof(BoardSummary))]
[JsonSerializable(typeof(BoardCursor))]
[JsonSerializable(typeof(BoardQuestionCursor))]
[JsonSerializable(typeof(BoardCounts))]
[JsonSerializable(typeof(BoardWatermark))]
[JsonSerializable(typeof(BoardPageArgs))]
[JsonSerializable(typeof(BoardPageResult))]
[JsonSerializable(typeof(BoardTaskArgs))]
[JsonSerializable(typeof(BoardTaskResult))]
[JsonSerializable(typeof(BoardChangesArgs))]
[JsonSerializable(typeof(BoardChangesResult))]
[JsonSerializable(typeof(BoardChange))]
[JsonSerializable(typeof(BoardAnswerValue))]
[JsonSerializable(typeof(BoardAnswerArgs))]
[JsonSerializable(typeof(BoardAnswerResult))]
[JsonSerializable(typeof(BoardQuestionAsk))]
[JsonSerializable(typeof(BoardAnswerReceived))]
[JsonSerializable(typeof(BoardUpdateArgs))]
[JsonSerializable(typeof(BoardUpdateResult))]
public partial class WorkflowJson : JsonSerializerContext;
