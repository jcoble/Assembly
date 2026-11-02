using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

public sealed record AuthTerminalDelivery(string MessageId, string EffectId, int ClaimAttempt,
    string ClientId, long UserRowSeq, string UserRunId);

public enum AuthOpenFailureStage { Initialize, NewSession, ResumeSession }

/// <summary>Retained identity of one actual auth terminal; version2 has a closed pre-identity opening stage.</summary>
public sealed record AuthTerminalProof(int Version, Guid ObservationId, Guid BlockCommandId, string ChatRunId,
    string? AcpSessionId, long Epoch, string? ClientId, string? ProviderMessageId, AuthTerminalDelivery? Delivery,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthOpenFailureStage? OpenFailure = null);
