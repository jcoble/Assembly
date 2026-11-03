namespace Assembly.Application.Common.Engine;

/// <summary>Owns the existing session semaphore until the maintenance operation finishes.</summary>
public interface ISessionMaintenance
{
    SessionMaintenanceAttempt TryAcquire(string sessionId);
}
public sealed record SessionMaintenanceAttempt(ISessionMaintenanceLease? Lease, string? Reason);
public interface ISessionMaintenanceLease : IAsyncDisposable
{
    Task<bool> ProbeResumeAsync(string providerSessionId, string folder, CancellationToken ct);
}
