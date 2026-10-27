using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// An adapter's prompts for a session no session has claimed (each open session's host answers its own, through
/// <see cref="AcpClient.SetSessionPolicy"/>): nobody can answer, so they are cancelled.
/// </summary>
public sealed class UnclaimedPermissions : IPermissionPolicy
{
    public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PermissionOutcome.Cancelled);
}
