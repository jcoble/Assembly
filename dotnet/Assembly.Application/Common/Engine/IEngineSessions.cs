using Assembly.Application.Common.Database;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Engine;

/// <summary>Owning Engine session operations inside the caller's transaction.</summary>
public interface IEngineSessions
{
    void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId);
    void Titled(IWriteScope scope, string sessionId, string actualStoredTitle, ReadOnlyMemory<byte> exactTitleUtf8);
    void Configured(IWriteScope scope, string sessionId, string? model);
    SessionLocation? Location(string sessionId);
    SessionLocation? Location(IReadScope scope, string sessionId);
    string? Folder(string sessionId);
    string? Folder(IReadScope scope, string sessionId);
}
