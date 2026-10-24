using Assembly.Engine.Acp;
using Assembly.Engine.Acp.Messages;

namespace Assembly.Engine.Sessions;

/// <summary>
/// An adapter's prompts for a session no thread has claimed (each open session's host answers its own, through
/// <see cref="AcpClient.SetSessionPolicy"/>): nobody can answer, so they are cancelled.
/// </summary>
public sealed class UnclaimedPermissions : IPermissionPolicy
{
    public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PermissionOutcome.Cancelled);
}
