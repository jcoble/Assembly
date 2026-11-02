using System.Text.Json;
using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

internal enum ParentNeedPhaseWrite { Prepare, Dispatch, Outcome }

/// <summary>Frozen bytes for one closed source transition, built from an actual owning snapshot outside its writer.</summary>
internal sealed class PreparedParentNeedPhase
{
    internal string IntentId { get; }
    internal long ExpectedVersion { get; }
    internal string ExpectedState { get; }
    internal ParentNeedPhase ExpectedPhase { get; }
    internal ParentNeedSource Source { get; }
    internal string InputSha256 { get; }
    internal string SourceJson { get; }
    internal string? ExpectedDescriptorJson { get; }
    internal string NextJson { get; }
    internal string Key { get; }
    internal string Canonical { get; }
    internal ParentNeedPhaseWrite Write { get; }
    internal NeedAnswerRequestDescriptor? Descriptor { get; }
    internal ParentNeedOutcome? Outcome { get; }

    private PreparedParentNeedPhase(ParentNeedSourceCapture capture, ParentNeedIntentPayload original,
        ParentNeedIntentPayload next, ParentNeedPhaseWrite write, string suffix, CancellationToken ct)
    {
        IntentId = capture.IntentId; ExpectedVersion = capture.Version; ExpectedState = capture.State;
        ExpectedPhase = original.Phase; Source = original.Source; InputSha256 = original.InputSha256;
        SourceJson = JsonSerializer.Serialize(Source, EngineJson.Default.ParentNeedSource);
        ExpectedDescriptorJson = original.Prepared is { } before
            ? JsonSerializer.Serialize(before.Descriptor, EngineJson.Default.NeedAnswerRequestDescriptor) : null;
        Descriptor = next.Prepared?.Descriptor; Outcome = next.Outcome; Write = write;
        NextJson = JsonSerializer.Serialize(next, EngineJson.Default.ParentNeedIntentPayload);
        Key = ParentNeedCodec.SourceKey(Source) + suffix;
        using var hash = new RelayHash();
        hash.String("assembly-parent-need-phase/v1"); hash.String(Key); hash.String(InputSha256);
        hash.NullableString(Descriptor is null ? null : ParentNeedCodec.Identity(Descriptor));
        hash.NullableString(Outcome is null ? null : JsonSerializer.Serialize(Outcome, EngineJson.Default.ParentNeedOutcome));
        Canonical = "{\"kind\":\"parent_need_phase\",\"identitySha256\":\"" + hash.Finish() + "\"}";
        ct.ThrowIfCancellationRequested();
    }

    internal static PreparedParentNeedPhase Prepare(ParentNeedSourceCapture capture, ParentNeedPrepared proposed, CancellationToken ct)
    {
        var original = capture.ReadPayload();
        var descriptor = proposed.Descriptor;
        var bytes = proposed.AnswerUtf8?.ToArray();
        if (descriptor.Source != original.Source || descriptor.Target.NeedId != original.Source.NeedId ||
            descriptor.Target.ParentSessionId != original.Source.ParentSessionId ||
            descriptor.Target.ParentOwnerMachine != original.Source.SourceMachine ||
            descriptor.Target.AddresseeSessionId != original.Source.ParentSessionId ||
            descriptor.Target.ChildOwnerMachine == original.Source.SourceMachine || descriptor.Target.ChildHolder != "local" ||
            descriptor.Target.ChildReservedEpoch is not null) throw new RelayRefused("bad_args");
        ParentNeedCodec.Identity(descriptor);
        if (original.Source.Action == ParentNeedAction.Escalate)
        {
            if (bytes is not null || descriptor.Answer is not null || descriptor.ProviderBinding is not null)
                throw new RelayRefused("bad_args");
        }
        else
        {
            if (bytes is null || descriptor.Answer is not { } manifest || manifest.Utf8Length != bytes.LongLength ||
                manifest.Sha256 != RelayHash.Sha(bytes)) throw new RelayRefused("bad_args");
            RelayHash.Utf8.GetString(bytes);
            using var parsed = JsonDocument.Parse(bytes);
            var expected = original.Input.Answer;
            if (descriptor.Target.ProviderBacked && descriptor.Target.Kind == "permission" && expected is { ValueKind: JsonValueKind.String })
                expected = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["optionId"] = expected.Value.Clone() },
                    EngineJson.Default.DictionaryStringJsonElement);
            if (expected is null || !JsonElement.DeepEquals(parsed.RootElement, expected.Value) ||
                descriptor.Target.ProviderBacked != (descriptor.ProviderBinding is not null)) throw new RelayRefused("bad_args");
        }
        var frozen = new ParentNeedPrepared(descriptor, bytes);
        var next = original with { Phase = ParentNeedPhase.Prepared, Prepared = frozen, Outcome = null };
        return new(capture, original, next, ParentNeedPhaseWrite.Prepare, "/prepare", ct);
    }

    internal static PreparedParentNeedPhase Dispatch(ParentNeedSourceCapture capture, string expectedH, CancellationToken ct)
    {
        var original = capture.ReadPayload();
        if (original.Prepared is not { } prepared || ParentNeedCodec.Identity(prepared.Descriptor) != expectedH)
            throw new RelayRefused("source_changed");
        return new(capture, original, original with { Phase = ParentNeedPhase.Executing, Outcome = null },
            ParentNeedPhaseWrite.Dispatch, "/dispatch", ct);
    }

    internal static PreparedParentNeedPhase Complete(ParentNeedSourceCapture capture, ParentNeedOutcome outcome, CancellationToken ct)
    {
        var original = capture.ReadPayload();
        var rawRefusal = original.Prepared is null && original.Phase == ParentNeedPhase.Raw &&
            outcome.Origin == "source" && outcome.Status == "refused";
        var identity = original.Prepared is { } prepared ? ParentNeedCodec.Identity(prepared.Descriptor) : original.InputSha256;
        if (original.Prepared is null && !rawRefusal || outcome.IdentitySha256 != identity ||
            outcome.Status is not ("recorded" or "refused" or "unknown") ||
            outcome.Origin is not ("target" or "source") || outcome.Origin == "source" &&
                (outcome.Status != "refused" || outcome.Result is not null || outcome.EffectId is not null ||
                    outcome.EffectState is not null || string.IsNullOrWhiteSpace(outcome.Error)) ||
            outcome.Status == "recorded" && outcome.Result is not { Applied: true, Error: null } ||
            outcome.Status == "refused" && outcome.Origin == "target" && outcome.Result is not { Applied: false, Error: not null } ||
            outcome.Status == "unknown" && (outcome.Result is not null || outcome.EffectId is not null || outcome.EffectState is not null) ||
            outcome.EffectId is not null && outcome.EffectId != "answer/" + original.Source.NeedId)
            throw new RelayRefused("bad_args");
        var phase = outcome.Status == "unknown" ? ParentNeedPhase.Executing : ParentNeedPhase.Outcome;
        return new(capture, original, original with { Phase = phase, Outcome = outcome }, ParentNeedPhaseWrite.Outcome,
            outcome.Status == "unknown" ? "/outcome/unknown" : "/outcome/final", ct);
    }
}
