using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>Compile-time JSON for the row payloads, camelCase like the UI's types. Optional fields are left out when null.</summary>
[JsonSerializable(typeof(TurnPayload))]
[JsonSerializable(typeof(UserPayload))]
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
[JsonSerializable(typeof(RowsFeature.PageArgs))]
[JsonSerializable(typeof(RowsFeature.ToolArgs))]
[JsonSerializable(typeof(RowsFeature.ToolDetail))]
[JsonSerializable(typeof(JsonElement))]
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
