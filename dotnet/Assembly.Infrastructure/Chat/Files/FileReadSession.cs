using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Bounded coherent read snapshots, owned by the socket that opened them. No timers or disk rereads per chunk.</summary>
public sealed class FileReadSession
{
    private const int MaxSnapshots = 4;
    private const long MaxHeldBytes = 32L * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<Key, Snapshot> snapshots = [];
    private readonly Dictionary<string, int> pending = [];
    private readonly HashSet<string> closed = [];
    private sealed record Key(string Connection, string Root, string Path);
    private sealed record Snapshot(byte[] Bytes, FileChunk Metadata, long Next);

    public long HeldBytes
    {
        get { lock (gate) return snapshots.Values.Sum(snapshot => (long)snapshot.Bytes.Length); }
    }

    public async Task<FileChunk> ReadAsync(string root, string path, long offset, string? revision,
        string connection, CancellationToken cancellationToken)
    {
        var key = new Key(connection, Path.GetFullPath(root), SessionPaths.ResolveFile(root, path));
        if (offset != 0)
        {
            lock (gate)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (closed.Contains(connection) || !snapshots.TryGetValue(key, out var snapshot) ||
                        snapshot.Metadata.Revision != revision || snapshot.Next != offset)
                        throw new CallFailed("revision_changed: this read snapshot is no longer available; restart the read");
                    var chunk = FileRead.Chunk(snapshot.Bytes, offset, snapshot.Metadata);
                    if (chunk.NextOffset is { } next)
                        snapshots[key] = snapshot with { Next = next };
                    else
                        snapshots.Remove(key);
                    return chunk;
                }
                catch
                {
                    snapshots.Remove(key);
                    throw;
                }
            }
        }
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (closed.Contains(connection))
                throw new CallFailed("this file connection is closed");
            snapshots.Remove(key);
            var count = snapshots.Keys.Count(item => item.Connection == connection);
            var inFlight = pending.GetValueOrDefault(connection);
            if (count + inFlight >= MaxSnapshots)
                throw new CallFailed("close another unfinished file read before opening this file");
            pending[connection] = inFlight + 1;
        }
        try
        {
            var bytes = await FileRead.CaptureAsync(root, path, cancellationToken);
            var chunk = FileRead.Chunk(bytes, 0);
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (closed.Contains(connection))
                    throw new CallFailed("this file connection is closed");
                if (chunk.NextOffset is { } next)
                {
                    var held = snapshots.Where(item => item.Key.Connection == connection).Sum(item => (long)item.Value.Bytes.Length);
                    if (held + bytes.Length > MaxHeldBytes)
                        throw new CallFailed("unfinished file reads exceed the 32 MiB connection limit");
                    snapshots[key] = new Snapshot(bytes, chunk with { Content = "" }, next);
                }
            }
            return chunk;
        }
        finally
        {
            lock (gate)
            {
                var remaining = pending.GetValueOrDefault(connection) - 1;
                if (remaining > 0)
                    pending[connection] = remaining;
                else
                {
                    pending.Remove(connection);
                    closed.Remove(connection);
                }
            }
        }
    }

    public bool Release(string root, string path, string revision, string connection)
    {
        var key = new Key(connection, Path.GetFullPath(root), SessionPaths.ResolveFile(root, path));
        lock (gate)
        {
            if (!snapshots.TryGetValue(key, out var snapshot) || snapshot.Metadata.Revision != revision)
                return false;
            return snapshots.Remove(key);
        }
    }

    public void Close(string connection)
    {
        lock (gate)
        {
            foreach (var key in snapshots.Keys.Where(key => key.Connection == connection).ToArray())
                snapshots.Remove(key);
            // Retain a tombstone only while captures can still complete.
            if (pending.ContainsKey(connection))
                closed.Add(connection);
        }
    }
}
