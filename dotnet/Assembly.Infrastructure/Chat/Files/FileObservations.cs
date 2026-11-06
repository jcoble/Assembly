using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// The roots each connection's Files trees observe, by lease. With topics, a folder made, deleted or renamed under a
/// root is published on <c>filesChanged</c>, keyed by the lease, at most once per <see cref="Window"/>: the first
/// change opens the window and the folders changed during it go out together. Nothing waits while nothing changes,
/// and releasing the lease ends its window.
/// </summary>
internal sealed class FileObservations(ITopics? topics = null, BackgroundWork? work = null)
{
    public const string Topic = "filesChanged";
    internal static readonly TimeSpan Window = TimeSpan.FromMilliseconds(350);
    /// <summary>More folders than this in one window are sent as none, which reads as "reload anyway".</summary>
    private const int MostFolders = 32;

    private sealed class Observation(string connection, DirectoryGeneration.Lease lease)
    {
        public readonly string Connection = connection;
        public readonly DirectoryGeneration.Lease Lease = lease;
        public readonly CancellationTokenSource Stop = new();
        public IDisposable? Listening;
        /// <summary>The window's folders, or null when no window is open.</summary>
        public HashSet<string>? Folders;
        public bool Everything;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Observation> observations = [];

    public ObservedRoot Observe(string root, string connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lease = DirectoryGeneration.Shared.Observe(root);
        var id = Guid.NewGuid().ToString("N");
        var observation = new Observation(connection, lease);
        lock (gate) observations.Add(id, observation);
        if (topics is not null && work is not null) observation.Listening = lease.Listen(folder => Changed(id, observation, folder));
        ct.Register(() => Release(id, connection));
        return new ObservedRoot(id, lease.Generation);
    }

    /// <summary>Whether <paramref name="id"/> is a lease this connection holds.</summary>
    public bool Holds(string id, string connection)
    {
        lock (gate) return observations.TryGetValue(id, out var observation) && observation.Connection == connection;
    }

    private void Changed(string id, Observation observation, string? folder)
    {
        lock (gate)
        {
            if (observation.Stop.IsCancellationRequested) return;
            var opening = observation.Folders is null;
            observation.Folders ??= [];
            if (folder is null || observation.Folders.Count >= MostFolders && !observation.Folders.Contains(folder)) observation.Everything = true;
            else observation.Folders.Add(folder);
            if (!opening) return;
        }
        work!.Start("files changed " + id, async stop =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, observation.Stop.Token);
            try { await Task.Delay(Window, linked.Token); }
            catch (OperationCanceledException) { return; }
            Publish(id, observation);
        });
    }

    private void Publish(string id, Observation observation)
    {
        string[] folders;
        lock (gate)
        {
            if (observation.Stop.IsCancellationRequested || observation.Folders is null) return;
            folders = observation.Everything ? [] : [.. observation.Folders.Order(StringComparer.Ordinal)];
            observation.Folders = null;
            observation.Everything = false;
        }
        if (topics!.HasSubscribers(Topic, id))
            topics.Publish(Topic, id, JsonSerializer.Serialize(new FilesChanged(id, folders), FilesJson.Default.FilesChanged), keyedOnly: true);
    }

    public bool Release(string id, string connection)
    {
        Observation? observation;
        lock (gate)
        {
            if (!observations.TryGetValue(id, out observation) || observation.Connection != connection) return false;
            observations.Remove(id);
            observation.Stop.Cancel();
        }
        observation.Listening?.Dispose();
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

internal sealed record ObserveRoot(string Session, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
internal sealed record UnobserveRoot(string Lease, string? RelayScope = null, ExpectedSource? ExpectedSource = null, FileOwnership? ExpectedOwner = null);
internal sealed record ObservedRoot(string Lease, string Generation);
internal sealed record UnobservedRoot(bool Released);
/// <summary>The folders, from the root, whose entries changed under a lease; none means read the open folders again.</summary>
internal sealed record FilesChanged(string Lease, IReadOnlyList<string> Folders);
