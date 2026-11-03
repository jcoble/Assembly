using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Assembly.Engine.Contracts;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.NeedsMe;

public sealed record NeedOption(string Id, string Label, string Kind);
public sealed record NeedPresentation(string? Title, string? Recommendation,
    string InputMode, JsonElement? Schema, IReadOnlyList<NeedOption> Options, bool FreeText);

internal static class NeedPresentationInputs
{
    internal static long AllocateNumber(IWriteScope scope) => scope.QueryFirst("""
        UPDATE engine_meta SET next_need_number=next_need_number+1
        WHERE singleton=1 AND next_need_number>0 AND next_need_number<9223372036854775807
        RETURNING next_need_number-1
        """, row => (long?)row.Long(0)) ?? throw new InvalidOperationException("Need display numbers exhausted.");

    internal static NeedPresentation Normalize(OpenNeed command)
    {
        var supplied = command.Presentation;
        if (command.ProviderQuestion is { } card)
        {
            var options = card.TryGetProperty("options", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? entries.EnumerateArray().Select(option => new NeedOption(
                    option.GetProperty("optionId").GetString()!, option.GetProperty("name").GetString()!,
                    option.GetProperty("kind").GetString()!)).ToArray() : [];
            return new(Title(Text(card, "title"), command.Text), null,
                command.Kind == "permission" ? "permission" : "form",
                card.TryGetProperty("schema", out var schema) && schema.ValueKind != JsonValueKind.Null ? schema.Clone() : null,
                Array.AsReadOnly(options), false);
        }
        return new(Title(supplied?.Title, command.Text), supplied?.Recommendation,
            supplied?.InputMode ?? (command.Options.Count == 0 ? "text" : "choice"), supplied?.Schema?.Clone(),
            Array.AsReadOnly(supplied?.Options.ToArray() ?? command.Options.Select(option => new NeedOption(option, option, command.Kind)).ToArray()),
            supplied?.FreeText ?? command.Options.Count == 0);
    }

    internal static bool Valid(NeedPresentation presentation, OpenNeed command) =>
        (command.ProviderQuestion is not null || presentation.Title?.Length <= 120 && (presentation.Recommendation is null || presentation.Recommendation.Length <= 300)) &&
        presentation.InputMode is "permission" or "form" or "text" or "choice" &&
        presentation.Options.All(option => option.Id is not null && option.Label is not null && option.Kind is not null) &&
        presentation.Options.Select(option => option.Id).SequenceEqual(command.Options) &&
        presentation.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() == presentation.Options.Count &&
        (presentation.InputMode != "permission" || !presentation.FreeText) &&
        (presentation.InputMode != "form" || presentation.Schema is { ValueKind: JsonValueKind.Object });

    internal static bool FitsDisplay(OpenNeed command, CommandContext context, NeedPresentation presentation, long number, string? addressee)
    {
        var item = JsonSerializer.SerializeToNode(presentation, EngineJson.Default.NeedPresentation)!.AsObject();
        item["entityKind"] = "needs_me"; item["id"] = context.EntityId; item["version"] = 1;
        item["machine"] = context.Machine; item["sessionId"] = command.SessionId; item["runId"] = command.RunId;
        item["stepId"] = command.StepId; item["chatRowId"] = command.ChatRowId; item["addresseeSessionId"] = addressee;
        item["kind"] = command.Kind; item["text"] = command.Text; item["displayNumber"] = number;
        item["optionsJson"] = JsonSerializer.Serialize(command.Options, EngineJson.Default.IReadOnlyListString);
        item["state"] = "open"; item["createdMs"] = context.NowMs; item["updatedMs"] = context.NowMs;
        item["deadlineMs"] = command.DeadlineMs; item["taskUrl"] = null; item["answer"] = null;
        item["answerText"] = null; item["answeredBy"] = null; item["answeredMs"] = null; item["answeredOrigin"] = null;
        return Encoding.UTF8.GetByteCount(item.ToJsonString(EngineJson.Default.Options)) <= 32 * 1024;
    }

    internal static bool AcceptsMcp(NeedPresentation presentation, JsonElement answer) =>
        answer.ValueKind == JsonValueKind.String && (presentation.FreeText ||
            presentation.Options.Any(option => option.Id == answer.GetString()));

    private static string Title(string? supplied, string text) => supplied ??
        string.Concat(text.Split('\n', 2)[0].TrimEnd('\r').EnumerateRunes().Take(120).Select(rune => rune.ToString()));

    private static string? Text(JsonElement card, string name) =>
        card.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
