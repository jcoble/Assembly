using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Connection-owned cancellable search iterators. Pages continue the same scan, rather than rescanning old matches.</summary>
public sealed class SearchRun : IDisposable
{
    private sealed record Hit(NameMatch? Name, ContentMatch? Content);
    private sealed class Run(string id, string connection, string root, SearchArgs args, DirectoryGeneration.Lease lease)
    {
        public readonly string Id = id, Connection = connection, Root = root;
        public readonly SearchArgs Args = args;
        public readonly DirectoryGeneration.Lease Lease = lease;
        public readonly string Generation = lease.Generation;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Gate = new(1);
        public IAsyncEnumerator<Hit>? Iterator;
        public Hit? Pending;
        public int Skipped;
        public bool Truncated;
        public bool Ended;
        public Exception? CleanupError;
        public Task? Cleanup;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Run> runs = [];
    private readonly List<Task> closing = [];
    private bool disposed;

    public async Task<SearchResult> PageAsync(string root, SearchArgs args, string connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Query.Length > 1000 || args.Mode is not ("name" or "content")) throw new CallFailed("invalid search query or mode");
        root = SessionPaths.Resolve(root, "");
        Run run;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            closing.RemoveAll(task => task.IsCompletedSuccessfully);
            if (args.SearchId is not null)
            {
                if (!runs.TryGetValue(args.SearchId, out run!) || run.Connection != connection || run.Root != root ||
                    run.Args.Session != args.Session || run.Args.Query != args.Query || run.Args.Mode != args.Mode ||
                    run.Args.IncludeExcluded != args.IncludeExcluded) throw new CallFailed("cursor_changed: restart this search");
            }
            else
            {
                if (runs.Values.Count(item => item.Connection == connection) >= 4) throw new CallFailed("four file searches are already open");
                var id = Guid.NewGuid().ToString("N");
                run = new Run(id, connection, root, args, DirectoryGeneration.Shared.Observe(root));
                run.Iterator = FindAsync(run, run.Stop.Token).GetAsyncEnumerator(run.Stop.Token);
                runs.Add(id, run);
            }
        }
        using var cancellation = ct.Register(() => run.Stop.Cancel());
        await run.Gate.WaitAsync(CancellationToken.None);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (run.Ended || run.Lease.Generation != run.Generation) throw new CallFailed("cursor_changed: restart this search");
            var names = new List<NameMatch>();
            var content = new List<ContentMatch>();
            var bytes = 512;
            var limit = Math.Clamp(args.PageSize, 1, 200);
            var more = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var hit = run.Pending;
                run.Pending = null;
                if (hit is null)
                {
                    if (!await run.Iterator!.MoveNextAsync()) break;
                    hit = run.Iterator.Current;
                }
                var size = hit.Name is { } name ? JsonSerializer.SerializeToUtf8Bytes(name, FilesJson.Default.NameMatch).Length :
                    JsonSerializer.SerializeToUtf8Bytes(hit.Content!, FilesJson.Default.ContentMatch).Length;
                if (size > 1024 * 1024 - 512) throw new CallFailed("a search result is too large to display");
                if (names.Count + content.Count == limit || bytes + size + 1 > 1024 * 1024)
                { run.Pending = hit; more = true; break; }
                bytes += size + 1;
                if (hit.Name is { } found) names.Add(found); else content.Add(hit.Content!);
            }
            if (run.Lease.Generation != run.Generation) throw new CallFailed("cursor_changed: restart this search");
            var result = new SearchResult(run.Id, names, content, more, run.Skipped, run.Truncated);
            if (!more) await EndAsync(run);
            return result;
        }
        catch { await EndAsync(run); throw; }
        finally { run.Gate.Release(); }
    }

    private static async IAsyncEnumerable<Hit> FindAsync(Run run, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(run.Args.Query)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenBytes = 0;
        await foreach (var entry in WalkAsync(run, "", ct))
        {
            if (!seen.Add(entry.Path)) continue;
            seenBytes += Encoding.UTF8.GetByteCount(entry.Path);
            if (seenBytes > 2 * 1024 * 1024) { run.Truncated = true; yield break; }
            if (run.Args.Mode == "name")
            {
                if (NameSearch.Matches(entry.Name, run.Args.Query))
                    yield return new Hit(new NameMatch(entry.Path, entry.Name, entry.IsDirectory, entry.Path), null);
                continue;
            }
            if (entry.IsDirectory) continue;
            await using var file = await ContentSearch.OpenAsync(run.Root, entry.Path, ct);
            if (file is null) { run.Skipped++; continue; }
            await foreach (var match in ContentSearch.MatchesAsync(file, entry.Path, run.Args.Query, ct)) yield return new Hit(null, match);
        }
    }

    private static async IAsyncEnumerable<FileEntry> WalkAsync(Run run, string dir, [EnumeratorCancellation] CancellationToken ct)
    {
        PageCursor? cursor = null;
        do
        {
            FileListing? page = null;
            try { page = await DirectoryListing.ListAsync(run.Root, dir, run.Args.IncludeExcluded, ct, cursor: cursor); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException ||
                error is CallFailed failed && (failed.Message == "that folder isn't there" || failed.Message.Contains("goes through a link", StringComparison.Ordinal)))
            { run.Skipped++; }
            if (page is null) yield break;
            foreach (var entry in page.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Path.Length > 4096 || new FileInfo(Path.Combine(run.Root, entry.Path)).LinkTarget is not null) { run.Skipped++; continue; }
                yield return entry;
                if (entry.IsDirectory && entry.Name != ".git")
                    await foreach (var child in WalkAsync(run, entry.Path, ct)) yield return child;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private async Task EndAsync(Run run)
    {
        lock (gate) { runs.Remove(run.Id); run.Ended = true; }
        await run.Iterator!.DisposeAsync();
        run.Lease.Dispose();
    }

    public void Release(string id, string connection)
    {
        Run? run;
        lock (gate)
        {
            if (!runs.TryGetValue(id, out run) || run.Connection != connection) return;
            runs.Remove(id);
            run.Ended = true;
            run.Stop.Cancel();
            run.Cleanup = DisposeAfterTurnAsync(run);
            closing.Add(run.Cleanup);
        }
    }

    private async Task DisposeAfterTurnAsync(Run run)
    {
        await run.Gate.WaitAsync();
        try { await EndAsync(run); }
        catch (Exception error) { run.CleanupError = error; run.Lease.Dispose(); }
        finally { run.Gate.Release(); }
    }

    public async Task CloseAsync(string connection)
    {
        Close(connection);
        Task[] work;
        lock (gate) work = closing.ToArray();
        await Task.WhenAll(work);
    }

    public void Close(string connection)
    {
        string[] ids;
        lock (gate) ids = runs.Values.Where(run => run.Connection == connection).Select(run => run.Id).ToArray();
        foreach (var id in ids) Release(id, connection);
    }

    public void Dispose()
    {
        string[] connections;
        lock (gate) { disposed = true; connections = runs.Values.Select(run => run.Connection).Distinct().ToArray(); }
        foreach (var connection in connections) Close(connection);
    }
}
