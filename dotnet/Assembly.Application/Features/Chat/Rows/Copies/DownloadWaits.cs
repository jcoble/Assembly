namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>
/// The downloads running now, one signal each (TSK-1692). A page below a copy's oldest saved row waits on it: the next
/// chunk saved, the download's end, or <see cref="Longest"/>, whichever comes first. The hub owns it.
/// </summary>
public sealed class DownloadWaits(TimeProvider time)
{
    /// <summary>The longest a page waits for a download that stopped sending.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

    // Each running download's signal for its next chunk; replaced as each one fires.
    private readonly Dictionary<string, TaskCompletionSource> _running = [];

    public void Begin(string session)
    {
        lock (_running)
            _running.TryAdd(session, New());
    }

    /// <summary>The download ended, whole or cut: every page waiting on it reads again at once.</summary>
    public void End(string session)
    {
        lock (_running)
            if (_running.Remove(session, out var signal)) signal.TrySetResult();
    }

    /// <summary>A chunk of the session was saved.</summary>
    public void Saved(string session)
    {
        lock (_running)
            if (_running.TryGetValue(session, out var signal))
            {
                _running[session] = New();
                signal.TrySetResult();
            }
    }

    /// <summary>The signal a page waits on, taken before it reads, so a chunk saved meanwhile still wakes it. Null when none runs.</summary>
    public Task? Next(string session)
    {
        lock (_running)
            return _running.GetValueOrDefault(session)?.Task;
    }

    /// <summary>Waits for <paramref name="next"/> or <see cref="Longest"/>. Its timer is gone when this returns.</summary>
    public async Task WaitAsync(Task next, CancellationToken ct)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var longest = Task.Delay(Longest, time, done.Token);
        try { await Task.WhenAny(next, longest); }
        finally { await done.CancelAsync(); }
        ct.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
