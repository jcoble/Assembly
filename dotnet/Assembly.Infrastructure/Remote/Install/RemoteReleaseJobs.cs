using System.Text.Json.Serialization;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Updates;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteReleaseJob(string Machine, string State, string? ErrorCode, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteReleaseRecord))]
internal sealed partial class RemoteReleaseJson : JsonSerializerContext;

/// <summary>One install per machine at a time. Started and stopped with the backend.</summary>
internal sealed class RemoteReleaseJobs(RemoteReleaseInstall install,
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
            entry = new(new(machine, "running", null, null)) { Progress = progress };
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
            await act(machine, phase => entry.Progress?.Invoke(phase), linked.Token);
            await connect(machine.Name, linked.Token);
            Change(entry, job => job with { State = "succeeded" });
        }
        catch (OperationCanceledException) { Change(entry, job => job with { State = "cancelled", ErrorCode = "cancelled" }); }
        catch (CallFailed failed) { Change(entry, job => job with { State = "failed", ErrorCode = failed.Reason ?? "install_failed", Message = failed.Message }); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { Change(entry, job => job with { State = "failed", ErrorCode = "install_failed", Message = error.Message }); }
        finally { entry.Done.TrySetResult(); }
    }

    private void Change(Entry entry, Func<RemoteReleaseJob, RemoteReleaseJob> change)
    {
        lock (_gate) entry.Job = change(entry.Job);
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
