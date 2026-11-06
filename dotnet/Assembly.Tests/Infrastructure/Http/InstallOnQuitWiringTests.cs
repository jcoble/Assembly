using System.IO.Pipes;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Application.Common.Calls;
using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The real backend and its real shutdown, with only the install itself stood in for.</summary>
public sealed class InstallOnQuitWiringTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("quit-install-").FullName;
    private readonly StringWriter _log = new();

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private sealed class CountingInstall(Exception? fails = null) : IAppInstall
    {
        internal int Quits;
        internal readonly TaskCompletionSource Watched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailedVersion => null;
        public string? Note => null;
        public string? Ready => null;
        public Task StageAsync(string version, Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public Task RestartAsync(Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public void Started() { }
        public void Unstage() { }
        public Task WatchFirstStartAsync(CancellationToken ct) { Watched.TrySetResult(); return Task.CompletedTask; }
        public Task InstallOnQuitAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Quits);
            return fails is null ? Task.CompletedTask : Task.FromException(fails);
        }
    }

    private Task<DesktopBackend> Start(IAppInstall install) => DesktopBackend.StartAsync(HomeLock.Take(_home), _home, "token", 0, new FakeLauncher(), _log,
        null, null, null, null, null, null, null, null, null, install);

    [Fact]
    public async Task A_quit_stops_the_host_and_installs_what_is_ready()
    {
        var install = new CountingInstall();
        var backend = await Start(install);

        await backend.DisposeAsync();

        Assert.Equal(1, install.Quits);
    }

    [Fact]
    public async Task A_window_that_dies_ends_stdin_and_installs_nothing()
    {
        var install = new CountingInstall();
        var backend = await Start(install);
        using (var stdin = new AnonymousPipeServerStream(PipeDirection.Out))
        using (var reading = new AnonymousPipeClientStream(PipeDirection.In, stdin.ClientSafePipeHandle))
        {
            backend.StopAtEndOf(reading);
            stdin.Dispose();
            await backend.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }

        await backend.DisposeAsync();

        Assert.Equal(0, install.Quits);
    }

    [Theory]
    [InlineData("call")]
    [InlineData("io")]
    [InlineData("limit")]
    public async Task An_install_that_fails_at_quit_is_logged_and_the_backend_still_stops(string how)
    {
        Exception error = how switch { "call" => new CallFailed("no helper", "app_update_failed"), "io" => new IOException("no helper"), _ => new OperationCanceledException("no helper") };
        var backend = await Start(new CountingInstall(error));

        await backend.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("update on quit: no helper", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launch_starts_the_watch_for_its_first_window()
    {
        var install = new CountingInstall();
        await using var backend = await Start(install);

        backend.WatchFirstStart();

        await install.Watched.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
