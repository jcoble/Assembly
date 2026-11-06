namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Saves and file actions in two sessions sharing a path use the same host-side ordering.</summary>
internal static class FileMutationOrder
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1);
        public int Users;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    public static async Task<IDisposable> EnterAsync(string path, CancellationToken ct)
    {
        Entry entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out entry!)) Entries[path] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(ct); }
        catch { Forget(path, entry); throw; }
        return new Lease(path, entry);
    }

    private static void Forget(string path, Entry entry)
    {
        lock (Gate)
            if (--entry.Users == 0) { Entries.Remove(path); entry.Semaphore.Dispose(); }
    }

    private sealed class Lease(string path, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Semaphore.Release();
            Forget(path, entry);
        }
    }
}
