using Assembly.Application.Common.Engine;
using System.Text.Json.Serialization;
using System.Text.Json;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Effects;

public sealed record Effect(string Id, string Kind, string? SessionId, string? RunId,
    JsonElement Payload, int Attempt);
public enum EffectDisposition { Confirmed, TransientFailure, Unavailable, Failed, Unknown }
public sealed record EffectReceipt(EffectDisposition Disposition, JsonElement Data, string? Error);
public interface IEngineEffects
{
    Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken);
    Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken);
}
public sealed record SessionEffectData(string State, string? ProviderSessionId, string? Reason);
public sealed record AnswerEffectData(string ProviderRequestId);
public sealed record MessageEffectData(string State, string? ProviderTurnId, string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserRowReference? UserRow = null);
public sealed record HandoffEffectData(string State, long NextEpoch, string? Reason);
public abstract record EffectCommand : EngineCommand;
public sealed record ClaimEffect() : EffectCommand;
public sealed record RecoverEffect() : EffectCommand;
public sealed record EnableEffect() : EffectCommand;
public sealed record CompleteEffect(EffectReceipt Receipt) : EffectCommand;
