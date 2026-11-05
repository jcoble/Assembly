using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>The one figure on the Update button, across everything in a run, and calling the run off.</summary>
public sealed class UpdateProgressTests
{
    private const string Here = UpdateProgress.ThisMachine, Box = "box.example";
    private readonly MachineStatus _status = new(new Topics(), ["box"]);
    private readonly UpdateProgress _progress;

    public UpdateProgressTests() => _progress = new(_status);

    private int? Figure(string? machine = null) => _status.Items.First(item => item.Name == machine).UpdatePercent;
    private bool Cancellable(string? machine = null) => _status.Items.First(item => item.Name == machine).UpdateCancellable;

    [Fact]
    public void Sizes_that_are_known_combine_by_bytes_on_every_machine_in_the_run()
    {
        _progress.Begin(null, Here, 0, downloads: true);
        _progress.Begin("box", Box, 0, downloads: true);
        _progress.Download(Here, "app", 0, 400);
        _progress.Download(Box, "backend", 0, 600);
        _progress.Download(Here, "app", 100, 400);
        Assert.Equal(10, Figure());
        _progress.Download(Box, "backend", 100, 600);

        Assert.Equal(20, Figure());
        Assert.Equal(20, Figure("box"));
    }

    [Fact]
    public void With_no_sizes_it_goes_by_finished_items()
    {
        _progress.Begin(null, Here, 3, downloads: false);
        Assert.Equal(0, Figure());
        _progress.Piece(Here);
        Assert.Equal(33, Figure());
        _progress.Piece(Here);
        Assert.Equal(66, Figure());
    }

    [Fact]
    public void Items_with_no_size_count_as_much_as_a_download_does_on_average()
    {
        _progress.Begin(null, Here, 2, downloads: true);
        _progress.Download(Here, "app", 100, 100);
        Assert.Equal(33, Figure());
        _progress.Piece(Here);
        Assert.Equal(66, Figure());
    }

    [Fact]
    public void It_never_goes_backward_when_a_larger_download_joins()
    {
        _progress.Begin(null, Here, 0, downloads: true);
        _progress.Download(Here, "app", 50, 100);
        Assert.Equal(50, Figure());

        _progress.Begin("box", Box, 0, downloads: true);
        Assert.Equal(50, Figure("box"));
        _progress.Download(Box, "backend", 0, 900);
        Assert.Equal(50, Figure());
        _progress.Download(Box, "backend", 900, 900);
        Assert.Equal(95, Figure());
    }

    [Fact]
    public void It_never_says_100_while_anything_is_left_and_the_next_run_starts_at_nothing()
    {
        _progress.Begin(null, Here, 0, downloads: true);
        _progress.Begin("box", Box, 0, downloads: true);
        _progress.Download(Here, "app", 100, 100);
        _progress.Download(Box, "backend", 100, 100);
        Assert.Equal(99, Figure());

        _progress.End(Here);
        Assert.Equal(99, Figure("box"));
        _progress.End(Box);

        _progress.Begin(null, Here, 2, downloads: false);
        Assert.Equal(0, Figure());
    }

    [Fact]
    public void Cancel_stops_the_run_while_it_is_downloading()
    {
        var first = _progress.Begin(null, Here, 0, downloads: true);
        var second = _progress.Begin("box", Box, 0, downloads: true);
        Assert.True(Cancellable());
        Assert.True(Cancellable("box"));

        Assert.True(_progress.Cancel());

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public void Once_an_install_starts_there_is_no_cancel()
    {
        var first = _progress.Begin(null, Here, 0, downloads: true);
        var second = _progress.Begin("box", Box, 0, downloads: true);

        _progress.Installing(Here);

        Assert.False(Cancellable());
        Assert.False(Cancellable("box"));
        Assert.False(_progress.Cancel());
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void A_cancel_that_got_there_first_stops_the_install_from_starting()
    {
        _progress.Begin(null, Here, 0, downloads: true);
        Assert.True(_progress.Cancel());
        Assert.Throws<OperationCanceledException>(() => _progress.Installing(Here));
    }

    [Fact]
    public void A_run_with_nothing_to_download_offers_no_cancel()
    {
        _progress.Begin(null, Here, 1, downloads: false);
        Assert.False(Cancellable());
        Assert.False(_progress.Cancel());
    }

    private sealed class Source : IUpdateSource
    {
        internal int Updates;
        internal readonly TaskCompletionSource<CancellationToken> Running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) => Task.FromResult(new UpdateWanted(null, "2.0.0",
            new Dictionary<string, string?>(), "sources", new Dictionary<string, string?>()));
        public bool StartsRemoteUpdates(string machine) => true;
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Updates);
            Running.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(5, deadline.Token);
    }

    private MachineUpdates Updates(Source source, BackgroundWork work) => new(source, _status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
        (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null, _progress);

    [Fact]
    public async Task A_cancelled_update_goes_back_to_ready_and_the_button_can_start_it_again()
    {
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source();
        var updates = Updates(source, work);
        _status.Updates(null, [new("desktop", "Desktop", "1.0.0", "2.0.0", UpdateItem.Behind)]);
        Assert.True(updates.Start());
        await source.Running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, Figure());
        Assert.True(Cancellable());

        Assert.True(updates.Cancel());
        await Until(() => !_status.Items[0].Updating);

        var here = _status.Items[0];
        Assert.Null(here.UpdateNote);
        Assert.Null(here.UpdatePercent);
        Assert.False(here.UpdateCancellable);
        Assert.Equal(UpdateItem.Behind, here.Updates![0].State);
        Assert.False(updates.Cancel());

        Assert.True(updates.Start());
        await Until(() => source.Updates == 2);
    }

    [Fact]
    public async Task A_remote_cancelled_is_not_started_again_by_its_next_check()
    {
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source();
        var updates = Updates(source, work);
        _status.Reachable("box");
        _status.Backend("box", new(null, null, false, "1.0.0", "2.0.0"));
        _status.Updates("box", [new("backend", "Backend", "1.0.0", "2.0.0", UpdateItem.Behind)]);
        Assert.True(updates.Start());
        await source.Running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(updates.Cancel());
        await Until(() => !_status.Items[1].Updating);

        await updates.CheckAsync("box", default, answered: true);

        Assert.Equal(1, source.Updates);
    }

    [Fact]
    public async Task A_failure_says_which_piece_failed()
    {
        var failures = new List<string>();
        var release = new FailingRelease();
        await UpdateSources.RemotesAsync(release, [new("box", [
            new("backend", "Backend", "1.0.0", "2.0.0", UpdateItem.Behind),
            new("cli", "codex", "1.0.0", "2.0.0", UpdateItem.Behind)])], _ => { }, failures, default);

        Assert.Equal(["Backend: disk full", "codex: not installed with npm"], failures);
    }

    private sealed class FailingRelease : IReleaseInstall
    {
        public Task<PublishedVersions> PublishedAsync(CancellationToken ct) => Task.FromResult(new PublishedVersions(null, null, null));
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct, bool keepAdapters = false) => throw new CallFailed("disk full");
        public Task UpdateToolsAsync(string? machine, ToolsBehind tools, Action<string> progress, CancellationToken ct) => throw new CallFailed("codex: not installed with npm");
    }
}
