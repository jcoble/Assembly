using Assembly.Domain.Rows;

namespace Assembly.Application.Common.Agents.Auth;

/// <summary>Only the exact opening's actual ACP response boundaries can stamp a pre-identity auth rejection.</summary>
internal sealed class AuthOpenAttempt
{
    internal bool IdentityAccepted { get; private set; }
    internal AuthOpenFailureStage? Failure { get; private set; }

    internal void InitializeRejected() => Reject(AuthOpenFailureStage.Initialize);
    internal void NewSessionRejected() => Reject(AuthOpenFailureStage.NewSession);
    internal void ResumeSessionRejected() => Reject(AuthOpenFailureStage.ResumeSession);
    internal void AcceptedIdentity() { IdentityAccepted = true; Failure = null; }
    private void Reject(AuthOpenFailureStage stage) { if (!IdentityAccepted) Failure = stage; }
}
