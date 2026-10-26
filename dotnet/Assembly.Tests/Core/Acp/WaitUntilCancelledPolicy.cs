using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Tests.Core.Acp;

/// <summary>A user who never answers: the prompt stays open until the turn is cancelled.</summary>
internal sealed class WaitUntilCancelledPolicy : IPermissionPolicy
{
    public async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return PermissionOutcome.Cancelled;
    }
}
