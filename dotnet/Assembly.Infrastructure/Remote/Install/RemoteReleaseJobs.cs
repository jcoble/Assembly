using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteReleaseLatestArgs;
internal sealed record RemoteReleaseLatest(string Version, int ProtocolVersion);
internal sealed record RemoteReleaseJob(Guid OperationId, long Version, string Machine, string State, string Phase,
    string? ErrorCode, string? Message, RemoteReleaseReceipt? Result);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteReleaseRecord))]
[JsonSerializable(typeof(RemoteReleaseLatestArgs))]
[JsonSerializable(typeof(RemoteReleaseLatest))]
[JsonSerializable(typeof(RemoteReleaseJob))]
internal sealed partial class RemoteReleaseJson : JsonSerializerContext;

/// <summary>One install per machine at a time, its state pushed on the topic. Started and stopped with the backend.</summary>
internal sealed class RemoteReleaseJobs(RemoteReleaseInstall install, ITopics topics,
    Func<string, CancellationToken, SavedMachine?> profile, Func<string, CancellationToken, Task> connect,
    RemoteReleaseFeed? feed = null, LocalReleaseTools? local = null) : IReleaseInstall, IAsyncDisposable
{
    private sealed class Entry(RemoteReleaseJob job)
    {
        internal RemoteReleaseJob Job = job;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<string>? Progress;
        internal bool Terminal => Job.State is "succeeded" or "failed" or "cancelled";
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _disposed;

    internal RemoteReleaseJob? TryStatus(Guid id) { lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Job : null; }

    /// <summary>An update's way in, waited for: one job per machine.</summary>
    public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct, bool keepAdapters = false) =>
        WaitedAsync(machine, progress, async (saved, phase, stop) => await install.InstallLatestAsync(saved, phase, stop, keepAdapters), ct);

    public Task UpdateToolsAsync(string? machine, ToolsBehind tools, Action<string> progress, CancellationToken ct)
    {
        if (machine is not null)
            return WaitedAsync(machine, progress, async (saved, phase, stop) => { await install.UpdateToolsAsync(saved, tools, phase, stop); return null; }, ct);
        return local is null ? throw new CallFailed("This app can't update its own adapters and CLIs.", "local_update_unavailable") : local.UpdateAsync(tools, progress, ct);
    }

    public Task<PublishedVersions> PublishedAsync(CancellationToken ct) => feed?.PublishedAsync(ct) ?? Task.FromResult(new PublishedVersions(null, null, null));

    private async Task WaitedAsync(string machine, Action<string> progress, Func<SavedMachine, Action<string>, CancellationToken, Task<RemoteReleaseReceipt?>> act, CancellationToken ct)
    {
        var saved = profile(machine, ct) ?? throw new CallFailed("Unknown remote machine.", "unknown_machine");
        var id = Guid.NewGuid();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Values.Any(value => !value.Terminal && value.Job.Machine == machine)) throw new CallFailed($"An install on {machine} is already running.", "install_busy");
            entry = new(new(id, 1, machine, "running", "Starting…", null, null, null)) { Progress = progress };
            _entries.Add(id, entry);
        }
        await Run(entry, saved, act, ct);
        ct.ThrowIfCancellationRequested();
        if (entry.Job.State == "failed") throw new CallFailed(entry.Job.Message ?? "The install failed.", entry.Job.ErrorCode ?? "install_failed");
    }

    private async Task Run(Entry entry, SavedMachine machine, Func<SavedMachine, Action<string>, CancellationToken, Task<RemoteReleaseReceipt?>> act, CancellationToken hostStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancel.Token, hostStop);
        try
        {
            var receipt = await act(machine, phase => { entry.Progress?.Invoke(phase); Change(entry, job => job with { Phase = phase }); }, linked.Token);
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
