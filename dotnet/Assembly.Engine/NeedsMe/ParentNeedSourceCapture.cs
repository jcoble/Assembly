using System.Text.Json;
using Assembly.Engine.Relays;

namespace Assembly.Engine.NeedsMe;

/// <summary>A fixed owning outbox snapshot; fresh writes must still compare its persisted scalar facts.</summary>
internal sealed class ParentNeedSourceCapture
{
    internal string IntentId { get; }
    internal long Version { get; }
    internal string State { get; }
    internal long CreatedMs { get; }
    private readonly string _raw;

    private ParentNeedSourceCapture(string intentId, long version, string state, long createdMs, string raw)
    { IntentId = intentId; Version = version; State = state; CreatedMs = createdMs; _raw = raw; }

    internal ParentNeedIntentPayload ReadPayload() => JsonSerializer.Deserialize(_raw, EngineJson.Default.ParentNeedIntentPayload)!;
    internal byte[] Bytes() => RelayHash.Utf8.GetBytes(_raw);

    internal static ParentNeedSourceCapture Create(string machine, string intentId, long version, string state,
        long createdMs, byte[] raw, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = RelayHash.Utf8.GetString(raw);
        var payload = JsonSerializer.Deserialize(text, EngineJson.Default.ParentNeedIntentPayload);
        if (payload is null || payload.Source is null || payload.Input is null || payload.Format != 1 || payload.Source.SourceMachine != machine ||
            payload.Source.NeedId != payload.Input.NeedId || payload.Source.Action != payload.Input.Action ||
            !Enum.IsDefined(payload.Source.Action) || payload.Source.ParentEpoch < 0 ||
            !Enum.IsDefined(payload.Phase) || !RelayHash.IsSha(payload.InputSha256) ||
            RelayHash.Sha(JsonSerializer.SerializeToUtf8Bytes(payload.Input, EngineJson.Default.ParentNeedRequest)) != payload.InputSha256 ||
            intentId != Persistence.RelayReceiptKeys.ContextGuid(ParentNeedCodec.SourceKey(payload.Source)).ToString("N") + "-0" ||
            version < 0 || createdMs < 0 || (payload.Phase switch
            {
                ParentNeedPhase.Raw => state != "pending" || payload.Prepared is not null || payload.Outcome is not null,
                ParentNeedPhase.Prepared => state != "pending" || payload.Prepared is null || payload.Outcome is not null,
                ParentNeedPhase.Executing => state != "uncertain" || payload.Prepared is null,
                ParentNeedPhase.Outcome => state is not ("done" or "failed") || payload.Outcome is null,
                _ => true
            })) throw new RelayRefused("source_changed");
        if (payload.Prepared is { } prepared)
        {
            if (prepared.Descriptor.Source != payload.Source ||
                (prepared.AnswerUtf8 is null) != (prepared.Descriptor.Answer is null)) throw new RelayRefused("source_changed");
            if (prepared.AnswerUtf8 is { } bytes && (prepared.Descriptor.Answer!.Utf8Length != bytes.LongLength ||
                prepared.Descriptor.Answer.Sha256 != RelayHash.Sha(bytes))) throw new RelayRefused("source_changed");
            ParentNeedCodec.Identity(prepared.Descriptor);
        }
        if (payload.Outcome is { } outcome)
        {
            var identity = payload.Prepared is { } captured ? ParentNeedCodec.Identity(captured.Descriptor) : payload.InputSha256;
            if (outcome.IdentitySha256 != identity || outcome.Origin is not ("source" or "target") ||
                payload.Phase == ParentNeedPhase.Executing && (outcome.Origin != "target" || outcome.Status != "unknown" || outcome.Result is not null) ||
                payload.Phase == ParentNeedPhase.Outcome && outcome.Status is not ("recorded" or "refused") || payload.Prepared is null &&
                (outcome.Origin != "source" || outcome.Status != "refused" || outcome.Result is not null ||
                    outcome.EffectId is not null || outcome.EffectState is not null)) throw new RelayRefused("source_changed");
        }
        ct.ThrowIfCancellationRequested();
        return new(intentId, version, state, createdMs, text);
    }
}
