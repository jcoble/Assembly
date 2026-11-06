using Assembly.Engine.Contracts;

namespace Assembly.Engine.Handoffs;

public abstract record HandoffCommand : EngineCommand;
public sealed record RequestHandoff(string SessionId, string NewHolder) : HandoffCommand;
public sealed record HandoffOutcome(string State, long NextEpoch, string? Reason) : HandoffCommand;

public sealed record HandoffTransfer(string HandoffId, string SessionId, string NewHolder, long NextEpoch);

/// <summary>Release this source epoch's captured adapter. ProviderSessionId is the stored public ACP/disposal
/// identity only; it does not establish the provider's actual resume/file identity or retained native history.</summary>
public sealed record HandoffReleaseEffect(HandoffIdentity Identity, string Provider, string AccountContext,
    string? ProviderSessionId);

/// <summary>Positive completion of the exact captured adapter operation, produced only after owned disposal.
/// ProviderSessionId is public ACP/disposal identity, never a native file/resume witness.</summary>
public sealed record HandoffReleaseEffectData(HandoffIdentity Identity, string SessionId, long Epoch,
    string? ProviderSessionId, string AdapterOperationId, string ReleaseEffectId, int ReleaseAttempt);
