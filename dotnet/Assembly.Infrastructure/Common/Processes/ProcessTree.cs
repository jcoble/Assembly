using System.Collections.Concurrent;
using System.Diagnostics;

namespace Assembly.Infrastructure.Common.Processes;

/// <summary>
/// Stops a process and everything it started, then waits until all of it is gone. .NET's tree kill waits for none of
/// it, and a killed grandchild passes to init, which clears it a moment later; without the wait a stopped run could
/// return while something it started is still listed.
/// </summary>
internal sealed class ProcessTree(Process root)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private readonly ConcurrentQueue<int> _killed = new();

    public void Kill()
    {
        try
        {
            if (root.HasExited)
                return;
            foreach (var pid in ProcessGroup.Descendants(root.Id))
                _killed.Enqueue(pid);
            root.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It had already exited.
        }
    }

    /// <summary>
    /// Nothing tells this process when init clears one that isn't its child, so after a kill only, it looks every few
    /// milliseconds, for at most five seconds.
    /// </summary>
    public async Task GoneAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (_killed.Any(ProcessGroup.Listed) && Stopwatch.GetElapsedTime(started) < Limit)
            await Task.Delay(5);
    }
}
