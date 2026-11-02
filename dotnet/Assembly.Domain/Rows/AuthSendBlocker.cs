using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

/// <summary>The exact send refusal and need retained by its original owning user row.</summary>
public sealed record AuthSendBlocker(string EffectId, int OriginalClaimAttempt, Guid ObservationId,
    string NeedId, long NeedVersionAtBlock,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BlockCommandId = null);

public sealed record AuthRefusalReceipt(long MessageAttempt, AuthSendBlocker Blocker);
