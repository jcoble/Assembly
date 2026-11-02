using Assembly.Application.Common.Calls;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class RelayMessageParts
{
    private void Retire(Connection connection, IDisposable stage, CancellationTokenSource stop)
    {
        // Called with the short gate held. The runner cannot dispose before readiness is published.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = connection.Operations.Select(o => o.Completion.Task).ToArray();
        async Task CleanupStage()
        {
            await ready.Task.ConfigureAwait(false);
            try { await stop.CancelAsync().ConfigureAwait(false); } catch { /* Join every exact user even if cancellation callbacks fault. */ }
            await Task.WhenAll(users).ConfigureAwait(false);
            stage.Dispose();
        }
        _cleanups.Add(CleanupStage());
        ready.SetResult();
    }

}
