namespace Assembly.Infrastructure.Chat.Files;

/// <summary>One watcher per actively browsed root, released by its last view/query lease.</summary>
public sealed class DirectoryGeneration
{
    public static DirectoryGeneration Shared { get; } = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Root> roots = new(StringComparer.Ordinal);
    internal sealed class Root(string id, FileSystemWatcher watcher)
    {
        public readonly string Id = id;
        public readonly FileSystemWatcher Watcher = watcher;
        public long Version;
        public int Leases;
        public TaskCompletionSource Changed = NewSignal();
        public string Generation => Id + ":" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Lease Observe(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (gate)
        {
            if (!roots.TryGetValue(root, out var entry))
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                entry = new Root(Guid.NewGuid().ToString("N"), watcher);
                var captured = entry;
                watcher.Changed += (_, _) => Advance(root, captured);
                watcher.Created += (_, _) => Advance(root, captured);
                watcher.Deleted += (_, _) => Advance(root, captured);
                watcher.Renamed += (_, _) => Advance(root, captured);
                watcher.Error += (_, _) => Advance(root, captured);
                roots.Add(root, entry);
                try { watcher.EnableRaisingEvents = true; }
                catch { roots.Remove(root); watcher.Dispose(); throw; }
            }
            entry.Leases++;
            return new Lease(this, root, entry);
        }
    }

    private void Advance(string root, Root captured)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            if (!roots.TryGetValue(root, out var current) || current != captured) return;
            captured.Version++;
            signal = captured.Changed;
            captured.Changed = NewSignal();
        }
        signal.TrySetResult();
    }

    public sealed class Lease : IDisposable
    {
        private readonly DirectoryGeneration owner;
        private readonly string path;
        private readonly Root root;
        private int disposed;
        internal Lease(DirectoryGeneration owner, string path, Root root) => (this.owner, this.path, this.root) = (owner, path, root);
        public string Generation { get { lock (owner.gate) return root.Generation; } }
        public Task ChangedAsync(string generation, CancellationToken ct)
        {
            lock (owner.gate) return generation != root.Generation ? Task.CompletedTask : root.Changed.Task.WaitAsync(ct);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var last = false;
            lock (owner.gate)
            {
                if (--root.Leases == 0) { owner.roots.Remove(path); last = true; }
            }
            if (last) { root.Watcher.Dispose(); root.Changed.TrySetCanceled(); }
        }
    }
}
