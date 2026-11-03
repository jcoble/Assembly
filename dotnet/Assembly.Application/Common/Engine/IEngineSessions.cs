using Assembly.Application.Common.Projects;
using Assembly.Application.Common.Database;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Engine;

/// <summary>Owning Engine session operations inside the caller's transaction.</summary>
public interface IEngineSessions
{
    void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId,
        string? projectId = null, CheckoutIntent? checkout = null, SessionCreationPreparation? preparation = null);
    void Titled(IWriteScope scope, string sessionId, string actualStoredTitle, ReadOnlyMemory<byte> exactTitleUtf8);
    void Configured(IWriteScope scope, string sessionId, string? model);
    SessionLocation? Location(string sessionId);
    SessionLocation? Location(IReadScope scope, string sessionId);
    string? Folder(string sessionId);
    string? Folder(IReadScope scope, string sessionId);
}

/// <summary>Authenticated host preparation; not a wire DTO or proof of the attributed sender's identity.</summary>
public sealed class SessionCreationPreparation
{
    internal string TargetMachine { get; }
    internal string FromMachine { get; }
    internal string ClientId { get; }
    internal string ProjectId { get; }
    internal string CanonicalRoot { get; }
    internal SessionCreationPreparation(string targetMachine, string fromMachine, string clientId, string projectId, string canonicalRoot)
        => (TargetMachine, FromMachine, ClientId, ProjectId, CanonicalRoot) =
            (targetMachine, fromMachine, clientId, projectId, canonicalRoot);
}
