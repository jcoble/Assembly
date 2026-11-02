namespace Assembly.Domain.Rows;

/// <summary>One real safe retry execution; its original user row keeps its original run identity.</summary>
public sealed record AuthRetryExecution(string ChatRunId, string MessageId, string EffectId, int ClaimAttempt,
    string PriorEffectId, string ClientId, long Epoch, long UserRowSeq, string UserRunId);
