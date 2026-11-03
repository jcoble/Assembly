using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteInstallTests
{
    [Theory]
    [InlineData("refused", "failed", "activation_refused")]
    [InlineData("rolled_back", "failed", "activation_rolled_back")]
    [InlineData("uncertain", "uncertain", "activation_uncertain")]
    [InlineData("mismatch", "uncertain", "activation_uncertain")]
    [InlineData("ssh_unavailable", "failed", "ssh_unavailable")]
    [InlineData("succeeded", "succeeded", null)]
    public async Task Settlement_preserves_primary_outcome_when_remote_cleanup_fails(string outcome, string state, string? error)
    {
        var process = new SettlementProcess(outcome) { FailCleanup = true };
        var activation = new RemoteInstallActivation(process);
        var jobs = new RemoteInstallJobs(_work, _topics, TimeProvider.System, Staging,
            (_, _, _) => Task.FromResult(Binding), process, (request, binding, package, staged, ct) => outcome == "mismatch"
                ? Task.FromResult(new RemoteInstallResult(package.Bundle.Commit, request.Machine, "unverified-engine"))
                : activation.ActivateAsync(request, binding, package, staged, ct));
        var terminal = new TaskCompletionSource<RemoteInstallJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = await SettlementRequest();
        using var observed = _topics.Subscribe("remoteInstall", request.OperationId.ToString("D"), _ => { }, (_, json) =>
        {
            var job = JsonSerializer.Deserialize(json, RemoteInstallJson.Default.RemoteInstallJob)!;
            if (job.State is "succeeded" or "failed" or "uncertain") terminal.TrySetResult(job);
        });
        try
        {
            _db.Writer.Write("test.install.settlement", scope => jobs.Admit(request, Binding, scope));
            var settled = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(state, settled.State); Assert.Equal(error, settled.ErrorCode);
            Assert.Equal(1, process.Cleanups);
            if (outcome == "succeeded")
                Assert.Equal(new RemoteInstallResult(new('a', 40), request.Machine, RemoteInstallTarget.Engine), settled.Result);
            else Assert.Null(settled.Result);
            Assert.Equal(settled, jobs.Status(request.OperationId));
            Assert.False(File.Exists(process.Snapshot));
        }
        finally { await jobs.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Settlement_releases_lifetime_when_snapshot_cleanup_and_terminal_subscriber_throw()
    {
        var process = new SettlementProcess("succeeded") { DenySnapshotCleanup = true };
        var activation = new RemoteInstallActivation(process);
        var jobs = new RemoteInstallJobs(_work, _topics, TimeProvider.System, Staging,
            (_, _, _) => Task.FromResult(Binding), process, activation.ActivateAsync);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = await SettlementRequest();
        using var observed = _topics.Subscribe("remoteInstall", request.OperationId.ToString("D"), _ => { }, (_, json) =>
        {
            var job = JsonSerializer.Deserialize(json, RemoteInstallJson.Default.RemoteInstallJob)!;
            if (job.State != "succeeded") return;
            published.TrySetResult();
            throw new InvalidOperationException("terminal subscriber failed");
        });
        try
        {
            _db.Writer.Write("test.install.settlement.publish", scope => jobs.Admit(request, Binding, scope));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<InvalidOperationException>(() => _db.Writer.Write<int>("test.install.next.rollback", scope =>
            { jobs.Admit(request with { OperationId = Guid.NewGuid() }, Binding, scope); throw new InvalidOperationException("rollback"); }));
            await jobs.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var settled = jobs.Status(request.OperationId);
            Assert.Equal("succeeded", settled.State); Assert.Null(settled.ErrorCode); Assert.NotNull(settled.Result);
            Assert.Equal(1, process.Cleanups);
            if (!OperatingSystem.IsWindows())
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(Path.GetDirectoryName(process.Snapshot)!));
        }
        finally
        {
            process.RestoreSnapshotAccess();
            await jobs.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private async Task<RemoteInstallStart> SettlementRequest()
    {
        var bundle = await Bundle();
        var selected = await RemoteInstallPackage.InspectAsync(bundle, default);
        return new(Guid.NewGuid(), "workbox-test", RemoteInstallTarget.Engine, bundle, selected.Bundle.Sha256);
    }

    private sealed class SettlementProcess(string outcome) : RemoteInstallProcess(TimeProvider.System)
    {
        internal bool FailCleanup, DenySnapshotCleanup;
        internal int Cleanups;
        internal string Snapshot = "";
        private FileStream? _held;
        internal override Task<RemoteInstallStaged> StageAsync(RemoteInstallStart request, RemoteInstallBinding binding,
            RemoteInstallPackage.Checked package, string snapshotFile, Action<long> progress, CancellationToken ct)
        {
            Snapshot = snapshotFile;
            if (outcome == "ssh_unavailable") throw new CallFailed("Fixture SSH failure.", "ssh_unavailable");
            return Task.FromResult(new RemoteInstallStaged(123, new('a', 64)));
        }
        internal override Task<JsonElement> ActivateAsync(RemoteInstallStart request, RemoteInstallBinding binding,
            RemoteInstallPackage.Checked package, RemoteInstallStaged staged, CancellationToken ct) => Task.FromResult(
                JsonSerializer.SerializeToElement(new { state = outcome, commit = package.Bundle.Commit, engine = binding.Engine, protocol = 1, format = 5 }));
        internal override Task CleanupAsync(RemoteInstallStart request, RemoteInstallBinding binding, CancellationToken ct)
        {
            Cleanups++;
            if (DenySnapshotCleanup)
            {
                if (OperatingSystem.IsWindows()) _held = new(Snapshot, FileMode.Open, FileAccess.Read, FileShare.None);
                else File.SetUnixFileMode(Path.GetDirectoryName(Snapshot)!, UnixFileMode.None);
            }
            if (FailCleanup) throw new IOException("Fixture cleanup failed.");
            return Task.CompletedTask;
        }
        internal void RestoreSnapshotAccess()
        {
            _held?.Dispose();
            if (!OperatingSystem.IsWindows() && Directory.Exists(Path.GetDirectoryName(Snapshot)))
                File.SetUnixFileMode(Path.GetDirectoryName(Snapshot)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
