using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Chat.SourceControl;

public sealed record GitOperationStart(string InstanceId, Guid OperationId, string? Session,
    string Action, JsonElement Args, string? Machine = null);
public sealed record GitOperationStatus(Guid OperationId, string InstanceId, string Machine,
    long Version, string State, JsonElement? Result, string? Error);
public sealed record GitOperationHostArgs(string? Session = null, string? Action = null, string? Machine = null);
public sealed record GitOperationArgs(Guid OperationId, string? Session = null, string? Action = null,
    string? InstanceId = null, string? Machine = null);

/// <summary>One backend-owned Git operation lifetime; routes select fixed producers before Start.</summary>
public sealed class GitOperations : IAsyncDisposable
{
    private sealed class Entry(GitOperationStart request, CancellationTokenSource deadline)
    {
        internal readonly GitOperationStart Request = request with { Args = request.Args.Clone() };
        internal readonly CancellationTokenSource Deadline = deadline;
        internal readonly CancellationTokenSource UserStop = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Cancelled = Task.CompletedTask;
        internal CancellationToken Safety;
        internal long Version = 1;
        internal string State = "accepted";
        internal JsonElement? Result;
        internal string? Error;
        internal bool Initialization, Mutation, Released, CancellationRequested;
        internal bool Terminal => State is "succeeded" or "failed" or "cancelled" or "uncertain";
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly HashSet<Task> _running = [];
    private readonly Dictionary<Guid, HashSet<string>> _watchers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly BackgroundWork _work;
    private readonly ITopics _topics;
    private readonly TimeProvider _time;
    private readonly string _machine;
    private bool _disposed;
    private Task? _disposing;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public GitOperations(BackgroundWork work, ITopics topics, TimeProvider time, string machine)
    {
        _work = work; _topics = topics; _time = time; _machine = machine;
        topics.Unsubscribed += Unsubscribed;
    }

    public GitOperationStatus Start(GitOperationStart request, Func<CancellationToken, Task<JsonElement>> execute)
    {
        Entry entry;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateStart(request);
            if (_entries.TryGetValue(request.OperationId, out var prior))
            {
                if (prior.Request.Action != request.Action || prior.Request.Session != request.Session ||
                    prior.Request.Machine != request.Machine || !JsonElement.DeepEquals(prior.Request.Args, request.Args))
                    throw new CallFailed("operation_id_conflict");
                return Snapshot(request.OperationId, prior);
            }
            entry = new(request, new(TimeSpan.FromSeconds(120), _time));
            _entries.Add(request.OperationId, entry);
            _running.Add(entry.Completion.Task);
            // BackgroundWork records this owned task before its first await can run the producer.
            _work.Start("git " + request.Action, async stopping =>
            {
                await ready.Task;
                try { await Run(request.OperationId, entry, execute, stopping); }
                finally
                {
                    lock (_gate) _running.Remove(entry.Completion.Task);
                    entry.Completion.TrySetResult();
                }
            });
        }
        var accepted = Snapshot(request.OperationId, entry);
        try { Publish(accepted); }
        finally { ready.TrySetResult(); }
        return accepted;
    }

    private void ValidateStart(GitOperationStart request)
    {
        if (request.InstanceId != InstanceId) throw new CallFailed("stale_instance");
        if (request.OperationId == Guid.Empty || request.Args.ValueKind != JsonValueKind.Object || request.Machine is not null)
            throw new CallFailed("bad_args");
        if (request.Action is not ("fetch" or "pull" or "push" or "publish" or "createPr" or "review" or "reply" or "comment" or "merge" or
            "draftCommitMessage" or "draftPr" or "draftReview" or "createProject" or "prepareProjectFolder")) throw new CallFailed("forbidden");
        if (request.Action is "createProject" or "prepareProjectFolder")
        {
            if (request.Session is not null) throw new CallFailed("bad_args");
        }
        else if (string.IsNullOrWhiteSpace(request.Session)) throw new CallFailed("bad_args");
    }

    public GitOperationStatus Status(Guid operationId)
    {
        lock (_gate) return _entries.TryGetValue(operationId, out var entry) ? Snapshot(operationId, entry)
            : new(operationId, InstanceId, _machine, 0, "uncertain", null, "operation_missing");
    }
    internal GitOperationStatus? Observe(Guid operationId, string connection)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_watchers.TryGetValue(operationId, out var watchers)) _watchers.Add(operationId, watchers = []);
            watchers.Add(connection);
            return _entries.TryGetValue(operationId, out var entry) ? Snapshot(operationId, entry) : null;
        }
    }
    internal void RequireScope(GitOperationArgs args)
    {
        lock (_gate)
        {
            if (args.InstanceId is not null && args.InstanceId != InstanceId) throw new CallFailed("stale_instance");
            if (!_entries.TryGetValue(args.OperationId, out var entry)) return;
            if (entry.Request.Session != args.Session ||
                (args.Action is not null && entry.Request.Action != args.Action) ||
                (entry.Request.Session is null && (args.InstanceId != InstanceId || args.Action != entry.Request.Action)))
                throw new CallFailed("forbidden");
        }
    }
    internal void FolderMutationDispatched(Guid operationId)
    {
        lock (_gate)
        {
            var entry = ProjectEntry(operationId);
            if (entry.CancellationRequested) throw new OperationCanceledException(entry.UserStop.Token);
            entry.UserStop.Token.ThrowIfCancellationRequested(); entry.Safety.ThrowIfCancellationRequested();
            entry.Mutation = true;
        }
    }
    internal CancellationToken InitializationDispatched(Guid operationId)
    {
        lock (_gate)
        {
            var entry = ProjectEntry(operationId);
            if (!entry.Request.Args.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "git") throw new CallFailed("forbidden");
            if (entry.CancellationRequested) throw new OperationCanceledException(entry.UserStop.Token);
            entry.UserStop.Token.ThrowIfCancellationRequested(); entry.Safety.ThrowIfCancellationRequested();
            entry.Initialization = true; entry.Mutation = true;
            return entry.Safety;
        }
    }
    private Entry ProjectEntry(Guid operationId)
    {
        if (!_entries.TryGetValue(operationId, out var entry) || entry.State != "running" ||
            entry.Request.Action is not ("createProject" or "prepareProjectFolder")) throw new CallFailed("operation_missing");
        return entry;
    }
    public bool Cancel(Guid operationId)
    {
        TaskCompletionSource ready;
        lock (_gate)
        {
            if (!_entries.TryGetValue(operationId, out var entry) || entry.Terminal) return false;
            if (entry.Initialization) throw new CallFailed("Repository initialization is already running. It will finish or reach its safety deadline.");
            if (!entry.Cancelled.IsCompleted || entry.UserStop.IsCancellationRequested) return true;
            entry.CancellationRequested = true;
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Cancelled = CancelAfter(ready.Task, entry.UserStop);
        }
        ready.SetResult(); return true;
    }
    private static async Task CancelAfter(Task ready, CancellationTokenSource stop)
    { await ready; await stop.CancelAsync(); }

    public void Release(Guid operationId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(operationId, out var entry)) return;
            entry.Released = true;
            if (entry.Terminal) _entries.Remove(operationId);
        }
    }
    internal void Closed(string connection)
    {
        lock (_gate)
        {
            foreach (var pair in _watchers.ToArray())
            {
                pair.Value.Remove(connection);
                if (pair.Value.Count != 0) continue;
                _watchers.Remove(pair.Key);
                if (_entries.TryGetValue(pair.Key, out var entry) && entry.Terminal) _entries.Remove(pair.Key);
            }
        }
    }
    private void Unsubscribed(string topic, string? key)
    {
        if (topic != "gitOperation" || key is null || !key.StartsWith(_machine + "/", StringComparison.Ordinal) ||
            !Guid.TryParseExact(key[(_machine.Length + 1)..], "D", out var id)) return;
        lock (_gate)
        {
            _watchers.Remove(id);
            if (_entries.TryGetValue(id, out var entry) && entry.Terminal) _entries.Remove(id);
        }
    }
    private async Task Run(Guid id, Entry entry, Func<CancellationToken, Task<JsonElement>> execute, CancellationToken stopping)
    {
        string state;
        string? error = null;
        JsonElement? result = null;
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(stopping, _stop.Token, entry.Deadline.Token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(safety.Token, entry.UserStop.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            GitOperationStatus running;
            lock (_gate)
            {
                if (entry.CancellationRequested) throw new OperationCanceledException(linked.Token);
                entry.Safety = safety.Token; entry.State = "running"; entry.Version++;
                if (entry.Request.Action is not ("createProject" or "prepareProjectFolder" or "draftCommitMessage" or "draftPr" or "draftReview")) entry.Mutation = true;
                running = Snapshot(id, entry);
            }
            Publish(running);
            result = (await execute(linked.Token)).Clone(); state = "succeeded";
        }
        catch (OperationCanceledException)
        {
            lock (_gate) state = entry.Mutation ? "uncertain" : "cancelled";
            error = state == "uncertain" ? "This may have completed. Refresh before trying again." : "Operation cancelled.";
        }
        catch (CallFailed failed) { state = "failed"; error = failed.Message.Length <= 65536 ? failed.Message : failed.Message[..65536] + "…"; }
        catch (Exception) { state = "failed"; error = "The Git operation failed."; }
        try
        {
            GitOperationStatus finished;
            Task cancellation;
            lock (_gate)
            {
                entry.State = state; entry.Result = result; entry.Error = error; entry.Version++;
                finished = Snapshot(id, entry); cancellation = entry.Cancelled;
                if (entry.Released || !_watchers.ContainsKey(id)) _entries.Remove(id);
            }
            await cancellation;
            Publish(finished);
        }
        finally
        {
            entry.Deadline.Dispose(); entry.UserStop.Dispose();
        }
    }
    private GitOperationStatus Snapshot(Guid id, Entry entry) => new(id, InstanceId, _machine, entry.Version, entry.State, entry.Result, entry.Error);
    internal string Key(Guid id) => _machine + "/" + id.ToString("D");
    private void Publish(GitOperationStatus status) => _topics.Publish("gitOperation", Key(status.OperationId),
        JsonSerializer.Serialize(status, SourceControlJson.Default.GitOperationStatus));

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? ready = null;
        Task disposal;
        lock (_gate)
        {
            if (_disposing is null)
            {
                _disposed = true; ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposing = DisposeAfter(ready.Task, _running.ToArray());
            }
            disposal = _disposing;
        }
        ready?.SetResult();
        return new(disposal);
    }
    private async Task DisposeAfter(Task ready, Task[] completion)
    {
        await ready;
        _topics.Unsubscribed -= Unsubscribed;
        await _stop.CancelAsync();
        await Task.WhenAll(completion);
        lock (_gate) { _entries.Clear(); _watchers.Clear(); }
        _stop.Dispose();
    }
}
