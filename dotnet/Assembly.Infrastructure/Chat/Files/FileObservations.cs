using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

internal sealed class FileObservations
{
    private sealed record Observation(string Connection, DirectoryGeneration.Lease Lease);
    private readonly object gate = new();
    private readonly Dictionary<string, Observation> observations = [];

    public ObservedRoot Observe(string root, string connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lease = DirectoryGeneration.Shared.Observe(root);
        var id = Guid.NewGuid().ToString("N");
        lock (gate) observations.Add(id, new Observation(connection, lease));
        ct.Register(() => Release(id, connection));
        return new ObservedRoot(id, lease.Generation);
    }

    public bool Release(string id, string connection)
    {
        Observation? observation;
        lock (gate)
        {
            if (!observations.TryGetValue(id, out observation) || observation.Connection != connection) return false;
            observations.Remove(id);
        }
        observation.Lease.Dispose();
        return true;
    }

    public void Close(string connection)
    {
        string[] ids;
        lock (gate) ids = observations.Where(item => item.Value.Connection == connection).Select(item => item.Key).ToArray();
        foreach (var id in ids) Release(id, connection);
    }
}

internal sealed record ObserveRoot(string? Session, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null, PathTarget? Target = null);
internal sealed record UnobserveRoot(string Lease, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
internal sealed record ObservedRoot(string Lease, string Generation);
internal sealed record UnobservedRoot(bool Released);
