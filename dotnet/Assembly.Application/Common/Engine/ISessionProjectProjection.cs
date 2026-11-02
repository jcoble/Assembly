using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Engine;

/// <summary>Chat identity and checkout read projection on the engine's supplied writer scope.</summary>
public interface ISessionProjectProjection
{
    void Assigned(IWriteScope scope, string sessionId, string? projectId);
    void CheckoutChanged(IWriteScope scope, string sessionId, string folder, string? branch);
}
