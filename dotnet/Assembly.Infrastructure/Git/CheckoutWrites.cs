namespace Assembly.Infrastructure.Git;

/// <summary>The one host-owned checkout writer lease, shared by checkout effects and Git features.</summary>
internal sealed class CheckoutWrites
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _roots = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string canonicalRoot, Func<Task<T>> work)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_roots.TryGetValue(canonicalRoot, out entry!)) _roots.Add(canonicalRoot, entry = new());
            entry.Users++;
        }
        await entry.Gate.WaitAsync();
        try { return await work(); }
        finally
        {
            entry.Gate.Release();
            lock (_gate)
            {
                if (--entry.Users == 0)
                {
                    _roots.Remove(canonicalRoot);
                    entry.Gate.Dispose();
                }
            }
        }
    }
}
