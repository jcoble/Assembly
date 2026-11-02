using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Persistence;
using Assembly.Engine.Messages;
using Assembly.Application.Common.Engine;

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
    internal OwnerPreparation? Owner { get; }

    private PreparedNeedCommand(NeedCommand command, PreparedTextJson canonical, OwnerPreparation? owner = null)
    {
        Command = command;
        Owner = owner;
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
        var frozen = Freeze(command, ct);
        return new(frozen, PreparedTextJson.CreateNeed(frozen, ct));
    }

    internal static PreparedNeedCommand PrepareOwner(Guid commandId, string machine, string needId, long version, long epoch,
        ResolveNeed original, PreparedProviderAnswer? captured, string? preparationError, CancellationToken ct)
    {
        if (preparationError is not (null or "answer_preparation_required" or "bad_args"))
            throw new ArgumentException("Unsupported owner answer preparation refusal.", nameof(preparationError));
        var frozen = (ResolveNeed)Freeze(original, ct);
        Text(machine); Text(needId);
        if (captured is not null)
        {
            captured = captured with { Answer = captured.Answer.Clone() };
            Json(captured.Answer, ct);
        }
        var identity = OwnerIdentity.Create(commandId, machine, needId, version, epoch);
        var canonical = EngineJournal.PrepareOwnerNeedCanonical(identity, frozen, ct);
        JsonElement? payload = captured is null ? null : JsonSerializer.SerializeToElement(
            new NeedAnswer(needId, captured.Binding.ProviderRequestId, captured.Answer, Binding: captured.Binding), EngineJson.Default.NeedAnswer);
        ResolutionNote? note = frozen.Answer is null ? null : PrepareResolutionNote(needId, frozen);
        ct.ThrowIfCancellationRequested();
        return new(frozen, canonical.Canonical, new(identity, captured, preparationError, canonical.Fingerprint, payload, note));
    }

    internal sealed class OwnerIdentity
    {
        internal Guid CommandId { get; }
        internal string Machine { get; }
        internal string NeedId { get; }
        internal long Version { get; }
        internal long Epoch { get; }
        private OwnerIdentity(Guid commandId, string machine, string needId, long version, long epoch) =>
            (CommandId, Machine, NeedId, Version, Epoch) = (commandId, machine, needId, version, epoch);
        internal static OwnerIdentity Create(Guid commandId, string machine, string needId, long version, long epoch) =>
            new(commandId, machine, needId, version, epoch);
    }

    internal sealed record OwnerPreparation(OwnerIdentity Identity, PreparedProviderAnswer? Captured,
        string? Error, string OriginalFingerprint, JsonElement? AnswerPayload, ResolutionNote? Note);
    internal sealed record ResolutionNote(string Text, byte[] Utf8);

    internal static ResolutionNote PrepareResolutionNote(string needId, ResolveNeed command)
    {
        var text = MessageTransitions.NeedResolutionText(needId, command.State, command.Answer);
        return new(text, Utf8.GetBytes(text));
    }

    private static NeedCommand Freeze(NeedCommand command, CancellationToken ct)
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
        return frozen;
    }

    private static void Text(string? text) { if (text is not null) Utf8.GetByteCount(text); }
    internal static void ValidateAnswer(JsonElement value, CancellationToken ct) => Json(value, ct);

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
