using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.NeedsMe;

/// <summary>Frozen full OpenNeed/ResolveNeed, with original small canonical bytes or one bounded hash envelope.</summary>
public sealed class PreparedNeedCommand
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal NeedCommand Command { get; }
    internal string? SmallCanonical { get; }
    internal string CanonicalSha256 { get; }
    internal long CanonicalUtf8Bytes { get; }
    internal string CanonicalJournal { get; }

    private PreparedNeedCommand(NeedCommand command, PreparedTextJson canonical)
    {
        Command = command;
        SmallCanonical = canonical.SmallCanonical;
        CanonicalSha256 = canonical.Sha256;
        CanonicalUtf8Bytes = canonical.Utf8Bytes;
        if (canonical.SmallCanonical is { } small) { CanonicalJournal = small; return; }
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("kind", canonical.Kind);
            json.WriteNumber("needJournalVersion", 1);
            json.WriteNumber("commandUtf8Bytes", canonical.Utf8Bytes);
            json.WriteString("commandSha256", canonical.Sha256); json.WriteEndObject();
        }
        CanonicalJournal = Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static PreparedNeedCommand Prepare(NeedCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frozen = command switch
        {
            OpenNeed open => open with
            {
                Options = open.Options is null ? null! : Array.AsReadOnly(open.Options.ToArray()),
                ProviderQuestion = open.ProviderQuestion?.Clone()
            },
            ResolveNeed resolve => (NeedCommand)(resolve with { Answer = resolve.Answer?.Clone() }),
            _ => throw new ArgumentException("Unsupported prepared need command.", nameof(command))
        };
        if (frozen is OpenNeed question)
        {
            Text(question.SessionId); Text(question.RunId); Text(question.StepId); Text(question.Kind);
            Text(question.Text); Text(question.ProviderRequestId); Text(question.ChatRowId);
            if (question.Options is not null) foreach (var option in question.Options) Text(option);
            if (question.ProviderQuestion is { } full) Json(full, ct);
        }
        else if (frozen is ResolveNeed answer)
        {
            Text(answer.State); Text(answer.Reason);
            if (answer.Answer is { } full) Json(full, ct);
        }
        return new(frozen, PreparedTextJson.CreateNeed(frozen, ct));
    }

    private static void Text(string? text) { if (text is not null) Utf8.GetByteCount(text); }
    private static void Json(JsonElement value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.String) Text(value.GetString());
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Json(child, ct);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) { Text(property.Name); Json(property.Value, ct); }
    }
}
