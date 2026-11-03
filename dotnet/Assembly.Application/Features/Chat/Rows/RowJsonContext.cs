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
[JsonSerializable(typeof(SubagentCursor.Value))]
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

// A continuation is scoped to one parent and captured source. It is never a raw item-id cursor.
internal static class SubagentCursor
{
    internal const int MaxChars = 64 * 1024;
    internal sealed record Value(int Version, string Session, ExpectedSource? Source, long Cut, string After);

    internal static Value Read(string token, string session, ExpectedSource? source)
    {
        if (token.Length > MaxChars) throw Invalid();
        Value value;
        try
        {
            var encoded = token.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            value = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), RowJsonContext.Rows.Value) ?? throw Invalid();
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw Invalid(); }
        if (value.Version != 1 || value.Session != session || value.Cut < 0 || value.After is null) throw Invalid();
        if (value.Source != source) throw new CallFailed("The subagent continuation source changed.", "source_changed");
        return value;
    }

    internal static string Write(string session, ExpectedSource? source, long cut, string after)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Value(1, session, source, cut, after), RowJsonContext.Rows.Value);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (token.Length > MaxChars) throw Invalid();
        return token;
    }

    internal static string? Rebind(string? token, string session, ExpectedSource from, ExpectedSource to)
    {
        if (token is null) return null;
        var value = Read(token, session, from);
        return Write(session, to, value.Cut, value.After);
    }

    private static CallFailed Invalid() => new("Invalid subagent continuation.", "bad_args");
}
