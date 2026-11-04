using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Remote.Install;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteReleaseStart(Guid OperationId, string Machine);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteReleaseOperation(Guid OperationId);
internal sealed record RemoteReleaseLatestArgs;
internal sealed record RemoteReleaseLatest(string Version, int ProtocolVersion);
internal sealed record RemoteReleaseJob(Guid OperationId, long Version, string Machine, string State, string Phase,
    string? ErrorCode, string? Message, RemoteReleaseReceipt? Result);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteReleaseRecord))]
[JsonSerializable(typeof(RemoteReleaseStart))]
[JsonSerializable(typeof(RemoteReleaseOperation))]
[JsonSerializable(typeof(RemoteReleaseLatestArgs))]
[JsonSerializable(typeof(RemoteReleaseLatest))]
[JsonSerializable(typeof(RemoteReleaseJob))]
internal sealed partial class RemoteReleaseJson : JsonSerializerContext;

/// <summary>The one install running at a time, its state pushed on the topic. Started and stopped with the backend.</summary>
internal sealed class RemoteReleaseJobs(RemoteReleaseInstall install, BackgroundWork work, ITopics topics,
    Func<string, CancellationToken, SavedMachine?> profile, Func<string, CancellationToken, Task> connect) : IAsyncDisposable
{
    private sealed class Entry(RemoteReleaseJob job)
    {
        internal RemoteReleaseJob Job = job;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Terminal => Job.State is "succeeded" or "failed" or "cancelled";
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;

    internal RemoteReleaseJob? TryStatus(Guid id) { lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Job : null; }

    internal RemoteReleaseJob Admit(RemoteReleaseStart request, IWriteScope scope)
    {
        if (request.OperationId == Guid.Empty) throw new CallFailed("Invalid install selection.", "bad_args");
        var machine = profile(request.Machine, CancellationToken.None) ?? throw new CallFailed("Unknown remote machine.", "unknown_machine");
        RemoteReleaseJob accepted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(request.OperationId, out var existing)) return existing.Job;
            if (_entries.Values.Any(value => !value.Terminal)) throw new CallFailed("A remote install is already running.", "install_busy");
            accepted = new(request.OperationId, 1, request.Machine, "running", "Starting…", null, null, null);
        }
        scope.AfterCommit(() =>
        {
            Entry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                entry = new(accepted);
                _entries.Add(request.OperationId, entry);
            }
            work.Start("remote release install", async hostStop => { await Task.Yield(); await Run(entry, machine, hostStop); });
        });
        scope.Publish(RemoteReleaseFeature.Name, request.OperationId.ToString("D"), accepted, RemoteReleaseJson.Default.RemoteReleaseJob);
        return accepted;
    }

    internal void Cancel(Guid id)
    {
        Entry? entry;
        lock (_gate) if (!_entries.TryGetValue(id, out entry) || entry.Terminal) return;
        work.Start("cancel remote release install", async _ => { await Task.Yield(); await entry.Cancel.CancelAsync(); });
    }

    private async Task Run(Entry entry, SavedMachine machine, CancellationToken hostStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancel.Token, hostStop);
        try
        {
            var receipt = await install.InstallLatestAsync(machine, phase => Change(entry, job => job with { Phase = phase }), linked.Token);
            Change(entry, job => job with { Phase = "Connecting…" });
            await connect(machine.Name, linked.Token);
            Change(entry, job => job with { State = "succeeded", Result = receipt });
        }
        catch (OperationCanceledException) { Change(entry, job => job with { State = "cancelled", ErrorCode = "cancelled" }); }
        catch (CallFailed failed) { Change(entry, job => job with { State = "failed", ErrorCode = failed.Reason ?? "install_failed", Message = failed.Message }); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { Change(entry, job => job with { State = "failed", ErrorCode = "install_failed", Message = error.Message }); }
        finally { entry.Done.TrySetResult(); }
    }

    private void Change(Entry entry, Func<RemoteReleaseJob, RemoteReleaseJob> change)
    {
        RemoteReleaseJob snapshot;
        lock (_gate) { entry.Job = change(entry.Job) with { Version = entry.Job.Version + 1 }; snapshot = entry.Job; }
        topics.Publish(RemoteReleaseFeature.Name, snapshot.OperationId.ToString("D"), JsonSerializer.Serialize(snapshot, RemoteReleaseJson.Default.RemoteReleaseJob));
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        Entry[] entries;
        lock (_gate) { _disposed = true; entries = [.. _entries.Values]; running = [.. entries.Select(entry => entry.Done.Task)]; }
        foreach (var entry in entries) await entry.Cancel.CancelAsync();
        await Task.WhenAll(running);
        foreach (var entry in entries) entry.Cancel.Dispose();
    }
}
