using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed class RemoteInstallJobs : IAsyncDisposable
{
    private sealed class Entry(RemoteInstallStart request, RemoteInstallBinding binding)
    {
        internal readonly RemoteInstallStart Request = request;
        internal readonly RemoteInstallBinding Binding = binding;
        internal readonly CancellationTokenSource Cancel = new();
        internal RemoteInstallJob Job = new(request.OperationId, 1, request.Machine, "accepted", "checking", 0, 0, null, null);
        internal TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Terminal => Job.State is "succeeded" or "failed" or "cancelled" or "uncertain";
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly BackgroundWork _work;
    private readonly ITopics _topics;
    private readonly TimeProvider _time;
    private readonly string _stagingRoot;
    private readonly Func<string, string, CancellationToken, Task<RemoteInstallBinding>> _capture;
    private readonly RemoteInstallProcess _process;
    // The activation producer is deliberately separate from staging. It must own actual restart exclusion/settlement.
    private readonly Func<RemoteInstallStart, RemoteInstallBinding, RemoteInstallPackage.Checked, RemoteInstallStaged, CancellationToken, Task<RemoteInstallResult>>? _activate;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private int _disposeStarted;
    private readonly TaskCompletionSource _disposeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static RemoteInstallJobs Create(string home, BackgroundWork work, ITopics topics, TimeProvider time, RemoteConnections connections)
    {
        if (!Path.IsPathFullyQualified(home)) throw new ArgumentException("An actual desktop home is required.", nameof(home));
        var process = new RemoteInstallProcess(time);
        var activation = new RemoteInstallActivation(process);
        return new(work, topics, time, Path.Combine(home, "staging", "remote-install"), connections.CaptureInstallOwnerAsync, process, activation.ActivateAsync);
    }

    internal RemoteInstallJobs(BackgroundWork work, ITopics topics, TimeProvider time, string stagingRoot,
        Func<string, string, CancellationToken, Task<RemoteInstallBinding>> capture, RemoteInstallProcess process,
        Func<RemoteInstallStart, RemoteInstallBinding, RemoteInstallPackage.Checked, RemoteInstallStaged, CancellationToken, Task<RemoteInstallResult>>? activate = null)
        => (_work, _topics, _time, _stagingRoot, _capture, _process, _activate) = (work, topics, time, stagingRoot, capture, process, activate);

    internal RemoteInstallJob? Existing(RemoteInstallStart request)
    {
        Validate(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(request.OperationId, out var entry)) return null;
            if (entry.Request != request) throw new CallFailed("Operation selection changed.", "operation_id_conflict");
            return entry.Job;
        }
    }
    internal async Task<RemoteInstallBinding> Capture(RemoteInstallStart request, CancellationToken ct)
    {
        Validate(request);
        var binding = await _capture(request.Machine, request.ExpectedEngine, ct);
        RemoteInstallTarget.Require(binding, ct);
        return binding;
    }
    internal RemoteInstallJob Status(Guid id)
    { lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Job : throw new CallFailed("Operation status is unknown.", "operation_unknown"); }
    internal RemoteInstallJob? TryStatus(Guid id)
    { lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Job : null; }

    internal RemoteInstallJob Admit(RemoteInstallStart request, RemoteInstallBinding binding, IWriteScope scope)
    {
        var existing = Existing(request);
        if (existing is not null) return existing;
        RemoteInstallJob accepted;
        lock (_gate)
        {
            if (_entries.Values.Any(value => !value.Terminal)) throw new CallFailed("A remote install is already running.", "install_busy");
            if (_entries.Count >= 64) throw new CallFailed("Install operation capacity reached.", "operation_capacity");
            accepted = new(request.OperationId, 1, request.Machine, "accepted", "checking", 0, 0, null, null);
        }
        scope.AfterCommit(() =>
        {
            Entry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                entry = new(request, binding);
                _entries.Add(request.OperationId, entry);
            }
            _work.Start("remote install", async hostStop => { await Task.Yield(); await Run(entry, hostStop); });
        });
        scope.Publish(RemoteInstallFeature.Name, request.OperationId.ToString("D"), accepted, RemoteInstallJson.Default.RemoteInstallJob);
        return accepted;
    }
    internal void Cancel(Guid id)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry!) || entry.Terminal) return;
            if (entry.Job.Phase is "activating" or "connecting") return;
        }
        // AfterCommit callbacks cannot synchronously run process/SSH cancellation callbacks.
        _work.Start("cancel remote install", async _ => { await Task.Yield(); await entry.Cancel.CancelAsync(); });
    }

    private async Task Run(Entry entry, CancellationToken hostStop)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancel.Token, hostStop, _stop.Token, deadline.Token);
        var ct = linked.Token;
        bool attempted = false, activated = false;
        string state = "failed";
        string? error = null;
        RemoteInstallResult? result = null;
        RemoteInstallPackage.Snapshot? snapshot = null;
        try
        {
            Change(entry, job => job with { State = "running" });
            snapshot = await RemoteInstallPackage.CopyAsync(_stagingRoot, entry.Request.BundleFile, entry.Request.Sha256, ct);
            var package = await RemoteInstallPackage.InspectAsync(snapshot.File, ct);
            if (package.Bundle.Sha256 != entry.Request.Sha256) throw new CallFailed("The selected bundle changed.", "bundle_changed");
            entry.Binding.RequireCurrent(ct);
            Change(entry, job => job with { Phase = "uploading", TotalBytes = package.Bundle.Bytes });
            attempted = true;
            var staged = await _process.StageAsync(entry.Request, entry.Binding, package, snapshot.File, bytes => Change(entry, job => job with { BytesDone = bytes }), ct);
            Change(entry, job => job with { Phase = "validating", BytesDone = package.Bundle.Bytes });
            entry.Binding.RequireCurrent(ct);
            ct.ThrowIfCancellationRequested();
            if (_activate is null) throw new CallFailed("Remote activation maintenance is unavailable.", "maintenance_unavailable");
            Change(entry, job => job with { Phase = "activating" });
            activated = true;
            // Once activation starts, user cancellation cannot strand the service stopped. The producer settles/rolls back.
            using var settle = new CancellationTokenSource(TimeSpan.FromMinutes(2), _time);
            var verified = await _activate(entry.Request, entry.Binding, package, staged, settle.Token);
            if (verified.Commit != package.Bundle.Commit || verified.Engine != entry.Binding.Engine || verified.Machine != entry.Request.Machine)
                throw new CallFailed("Remote activation could not be verified.", "activation_uncertain");
            result = verified;
            state = "succeeded";
        }
        catch (OperationCanceledException) { state = activated ? "uncertain" : "cancelled"; error = activated ? "activation_uncertain" : "cancelled"; }
        catch (CallFailed failed) { error = SafeError(failed.Reason); state = activated && error is not ("activation_rolled_back" or "activation_refused") ? "uncertain" : "failed"; }
        catch (Exception) { error = activated ? "activation_uncertain" : "install_failed"; state = activated ? "uncertain" : "failed"; }
        finally
        {
            try
            {
                // Cleanup cannot replace a proven activation outcome or the original failure.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20), _time);
                try { if (attempted) await _process.CleanupAsync(entry.Request, entry.Binding, cleanup.Token); }
                catch (Exception) { CleanupUnconfirmed(); }
                try { snapshot?.Dispose(); }
                catch (Exception failed) when (failed is IOException or UnauthorizedAccessException) { CleanupUnconfirmed(); }
            }
            finally
            {
                try { Change(entry, job => job with { State = state, ErrorCode = error, Result = result }); }
                finally { entry.Done.TrySetResult(); }
            }
        }
        void CleanupUnconfirmed()
        { if (error is null && result is null) { state = "uncertain"; error = "cleanup_unconfirmed"; } }
    }
    private void Change(Entry entry, Func<RemoteInstallJob, RemoteInstallJob> change)
    {
        RemoteInstallJob snapshot;
        lock (_gate) { entry.Job = change(entry.Job) with { Version = entry.Job.Version + 1 }; snapshot = entry.Job; }
        _topics.Publish(RemoteInstallFeature.Name, snapshot.OperationId.ToString("D"), JsonSerializer.Serialize(snapshot, RemoteInstallJson.Default.RemoteInstallJob));
    }
    private static void Validate(RemoteInstallStart request)
    {
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Machine) || request.ExpectedEngine != RemoteInstallTarget.Engine ||
            string.IsNullOrWhiteSpace(request.BundleFile) || !Path.IsPathFullyQualified(request.BundleFile) || !RemoteInstallPackage.Hex(request.Sha256, 64))
            throw new CallFailed("Invalid install selection.", "bad_args");
    }
    private static string SafeError(string? reason) => reason is "invalid_bundle" or "bundle_changed" or "target_changed" or
        "unsupported_target" or "remote_busy" or "ssh_unavailable" or "stage_failed" or "maintenance_unavailable" or
        "activation_rolled_back" or "activation_refused" or "activation_uncertain" ? reason : "install_failed";
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) { await _disposeDone.Task; return; }
        try
        {
            Task[] running;
            lock (_gate) { _disposed = true; running = _entries.Values.Select(entry => entry.Done.Task).ToArray(); }
            await _stop.CancelAsync();
            await Task.WhenAll(running);
            foreach (var entry in _entries.Values) entry.Cancel.Dispose();
            _stop.Dispose();
        }
        finally { _disposeDone.TrySetResult(); }
    }
}
