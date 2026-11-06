using Assembly.Engine.Contracts;

namespace Assembly.Engine.Providers;

public abstract record SignInCommand : EngineCommand;
public sealed record StartSignIn(string Provider, string AccountContext, string? MethodId, string? OriginNeedId) : SignInCommand;
public sealed record SignInProgress(string State, string? Reason, SignInAttemptBinding Attempt) : SignInCommand;
public sealed record CancelSignIn() : SignInCommand;
public sealed record ExpireSignIn() : SignInCommand;
public sealed record InterruptSignIn() : SignInCommand;
public sealed record SignInAttemptBinding(string OperationId, string TargetId, string BeginEffectId, int BeginClaimAttempt);
public sealed record BeginSignInEffect(string OperationId, string Provider, string? MethodId);
public sealed record CancelSignInEffect(string OperationId);
public sealed record SignInEffectData(string OperationId, string State);
