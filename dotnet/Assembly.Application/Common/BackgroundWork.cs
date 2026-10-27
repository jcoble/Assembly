using System.Collections.Concurrent;

namespace Assembly.Application.Common;

/// <summary>
/// Work that runs on its own, held until it ends, so a stop can cancel it and wait for it. A fault is logged as
/// <c>background name: message</c>, never left unobserved; a cancel by the stop isn't a fault. Work started after the
/// stop still runs, with a token already cancelled.
/// </summary>
public sealed class BackgroundWork(TimeProvider time, TextWriter log) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Task, string> _tasks = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>How many are still running; the shutdown line reads it too.</summary>
    public int Running => _tasks.Keys.Count(task => !task.IsCompleted);

    /// <summary>Runs <paramref name="work"/> with the stop token, and holds it until it ends.</summary>
    public void Start(string name, Func<CancellationToken, Task> work)
    {
        // Held before the work runs, so a stop during its first, synchronous part still waits for it. That part still
        // runs here, inline, so works started in order begin in order (a socket's answers rely on it).
        var start = new Task<Task>(() => RunAsync(name, work));
        var task = start.Unwrap();
        _tasks[task] = name;
        task.ContinueWith(ended => _tasks.TryRemove(ended, out _), TaskScheduler.Default);
        start.RunSynchronously(TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels the stop token, then waits up to <paramref name="deadline"/> for everything held, started meanwhile
    /// included. Returns how many were still running at the deadline, and names them in the log.
    /// </summary>
    public async Task<int> StopAsync(TimeSpan deadline)
    {
        using var timeout = new CancellationTokenSource(deadline, time);
        await _stop.CancelAsync();
        while (!timeout.IsCancellationRequested && _tasks.Keys.Where(task => !task.IsCompleted).ToArray() is { Length: > 0 } running)
        {
            try
            {
                await Task.WhenAll(running).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var left = _tasks.Where(pair => !pair.Key.IsCompleted).Select(pair => pair.Value).ToList();
        if (left.Count > 0)
            log.WriteLine($"shutdown: {left.Count} background tasks still running after {deadline.TotalSeconds:0.#} s: {string.Join(", ", left)}");
        return left.Count;
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(5));

    private async Task RunAsync(string name, Func<CancellationToken, Task> work)
    {
        try
        {
            await work(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            log.WriteLine($"background {name}: {error.Message}");
        }
    }
}
