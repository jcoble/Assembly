using Assembly.Engine.Acp;
using Assembly.Engine.Acp.Messages;

namespace Assembly.Tests.Acp;

/// <summary>A user who never answers: the prompt stays open until the turn is cancelled.</summary>
internal sealed class WaitUntilCancelledPolicy : IPermissionPolicy
{
    public async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return PermissionOutcome.Cancelled;
    }
}
