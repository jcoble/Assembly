namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One watcher per actively browsed root, released by its last view/query lease. Every change advances the root's
/// generation, which paging depends on. A file or folder made, deleted or renamed also tells the lease's listeners
/// which folder changed, so an open tree can read it again; a change in <c>.git</c> doesn't, since git churns there and
/// the tree never shows it. Other dot folders (<c>.github</c>, <c>.claude</c>) are told like any other.
/// </summary>
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
        /// <summary>Told the folder, from the root and "" for the root itself, or null when the watcher lost track.</summary>
        public readonly List<Action<string?>> Listeners = [];
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
                // A write changes a file, not the tree, so it only advances the generation.
                watcher.Changed += (_, _) => Advance(root, captured, null);
                watcher.Created += (_, e) => Advance(root, captured, [e.FullPath]);
                watcher.Deleted += (_, e) => Advance(root, captured, [e.FullPath]);
                watcher.Renamed += (_, e) => Advance(root, captured, [e.OldFullPath, e.FullPath]);
                watcher.Error += (_, _) => Advance(root, captured, []);
                roots.Add(root, entry);
                try { watcher.EnableRaisingEvents = true; }
                catch { roots.Remove(root); watcher.Dispose(); throw; }
            }
            entry.Leases++;
            return new Lease(this, root, entry);
        }
    }

    /// <summary><paramref name="shaped"/>: the paths made, deleted or renamed; empty when the watcher lost track.</summary>
    private void Advance(string root, Root captured, string[]? shaped)
    {
        TaskCompletionSource signal;
        Action<string?>[] listeners;
        lock (gate)
        {
            if (!roots.TryGetValue(root, out var current) || current != captured) return;
            captured.Version++;
            signal = captured.Changed;
            captured.Changed = NewSignal();
            listeners = [.. captured.Listeners];
        }
        signal.TrySetResult();
        if (shaped is null || listeners.Length == 0) return;
        string?[] folders = shaped.Length == 0 ? [null] : [.. shaped.Select(path => FolderOf(root, path)).OfType<string>().Distinct()];
        foreach (var folder in folders)
            foreach (var listener in listeners) listener(folder);
    }

    /// <summary>
    /// The folder holding <paramref name="path"/>, from the root with '/' between names and "" for the root; null outside
    /// the root, or for <c>.git</c> and anything in it.
    /// </summary>
    internal static string? FolderOf(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return null;
        if (relative == ".") return "";
        var names = relative.Split('/');
        return names.Contains(".git") ? null : string.Join('/', names[..^1]);
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

        /// <summary>Tells <paramref name="changed"/> each folder whose entries change, until disposed.</summary>
        public IDisposable Listen(Action<string?> changed)
        {
            lock (owner.gate) root.Listeners.Add(changed);
            return new Listening(owner, root, changed);
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

    private sealed class Listening(DirectoryGeneration owner, Root root, Action<string?> changed) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate) root.Listeners.Remove(changed);
        }
    }
}
