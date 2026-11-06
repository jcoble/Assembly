namespace Assembly.Engine.Sessions;

/// <summary>Inspection result only; the Engine retains authority over the operation and its outcome.</summary>
public sealed record CheckoutEffectData(string Folder, string? Branch, string? ProviderSessionId,
    string? Reason, bool Confirmed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Assembly.Application.Common.Engine.NativeResumeEvidence? NativeResume = null);

/// <summary>Immutable accepted intent. Initial start payload is retained without dispatching a start early.</summary>
public sealed record CheckoutOperation(string OperationId, long Epoch, string Folder, string? Branch,
    string? ProviderSessionId, ChangeCheckout Request, SessionStartEffect? InitialStart = null, string? InitialOperationId = null);
