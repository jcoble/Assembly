using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Domain.Rows;
using Assembly.Domain.Events;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>Compile-time JSON for the row payloads, camelCase like the UI's types. Optional fields are left out when null.</summary>
[JsonSerializable(typeof(ExpectedSource))]
[JsonSerializable(typeof(TurnPayload))]
[JsonSerializable(typeof(UserPayload))]
[JsonSerializable(typeof(UserAcknowledgment))]
[JsonSerializable(typeof(AuthTerminalProof))]
[JsonSerializable(typeof(AuthTerminalDelivery))]
[JsonSerializable(typeof(AuthOpenFailureStage))]
[JsonSerializable(typeof(AuthSendBlocker))]
[JsonSerializable(typeof(AuthRetryExecution))]
[JsonSerializable(typeof(AuthRefusalReceipt))]
[JsonSerializable(typeof(DeltaPayload))]
[JsonSerializable(typeof(MessagePayload))]
[JsonSerializable(typeof(ThinkingPayload))]
[JsonSerializable(typeof(ToolPayload))]
[JsonSerializable(typeof(PlanPayload))]
[JsonSerializable(typeof(StatsPayload))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(AskPayload))]
[JsonSerializable(typeof(TaskPayload))]
[JsonSerializable(typeof(ContextPayload))]
[JsonSerializable(typeof(SubagentPayload))]
[JsonSerializable(typeof(SubagentDisplay))]
[JsonSerializable(typeof(SubagentPage))]
[JsonSerializable(typeof(RowsFeature.SubagentArgs))]
[JsonSerializable(typeof(RowsFeature.PageArgs))]
[JsonSerializable(typeof(RowsFeature.ToolArgs))]
[JsonSerializable(typeof(RowsFeature.RawToolArgs))]
[JsonSerializable(typeof(StoredToolCall))]
[JsonSerializable(typeof(RowsFeature.ToolDetail))]
[JsonSerializable(typeof(RowsFeature.SessionArgs))]
[JsonSerializable(typeof(IReadOnlyList<FileTouched>))]
[JsonSerializable(typeof(SessionAttachments))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(TerminalExit))]
[JsonSerializable(typeof(IReadOnlyList<ToolTerminalState>))]
internal sealed partial class RowJsonContext : JsonSerializerContext
{
    /// <summary>
    /// The one to use. Text is written as is (an apostrophe stays <c>'</c>, not <c>'</c>): the UI parses
    /// this JSON and never pastes it into HTML, and escaping only adds bytes.
    /// </summary>
    public static RowJsonContext Rows { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
