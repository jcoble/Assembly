using Assembly.Domain.Rows;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>The one hole check that finishes a download (TSK-1692), on the same two hosts as the download itself.</summary>
public sealed partial class RemoteDownloadTests
{
    // Pins: spec test 7.
    [Fact]
    public async Task Spec07_a_copy_with_a_hole_equals_the_remote_after_the_check()
    {
        Seed("s", 350);
        _beforeDropping = seq => seq is >= 100 and <= 110;

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Contains(("after", "s"), _calls);
    }

    // Pins: spec test 8.
    [Fact]
    public async Task Spec08_trimmed_deltas_are_not_holes()
    {
        Seed("s", 100);
        for (var seq = 101; seq <= 103; seq++) Save("s", seq, "message", RowKind.Delta);
        Save("s", 104, "message", RowKind.Message);
        for (var seq = 105; seq <= 150; seq++) Save("s", seq, null);

        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s").Where(row => !row.StartsWith("10", StringComparison.Ordinal) || row.Split(' ')[0] is not ("101" or "102" or "103")),
            MacRows("s"));
        Assert.DoesNotContain(("after", "s"), _calls);
    }

    [Fact]
    public async Task A_check_cut_short_runs_again_on_the_next_connection()
    {
        Seed("s", 350);
        _beforeDropping = seq => seq is >= 100 and <= 110;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _afterReading = ct =>
        {
            reached.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await reached.Task.WaitAsync(Wait);

        await _hosts.Remote.Stop();
        Assert.False(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
        _afterReading = null;
        _beforeDropping = null;
        _bottomed.TryRemove("s", out _);
        await _hosts.Remote.Restart();
        await Machine.ProbeAsync(default).WaitAsync(Wait);
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
    }

    // Pins: review focus 2.
    [Fact]
    public async Task An_adopted_copy_is_checked_only_after_the_follower_reaches_head()
    {
        Seed("s", 350);
        Forward("s", 300);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await Bottomed("s").Task.WaitAsync(Wait);
        await SyncedAsync();
        Assert.False(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
        Assert.DoesNotContain(("after", "s"), _calls);

        _bottomed.TryRemove("s", out _);
        using var viewer = _hosts.MacHub.Subscribe("s", _hosts.MacHub.LastSeq("s"));
        Machine.WatchRun("s", "a-run-still-to-come");
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
    }

    [Fact]
    public async Task A_waiting_adopted_copy_does_not_hold_up_another_download()
    {
        Seed("s", 350);
        Forward("s", 300);
        Seed("t", 20);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await Bottomed("s").Task.WaitAsync(Wait);

        await Machine.EnsureCopyAsync("t", default).WaitAsync(Wait);

        Assert.True(_hosts.MacHub.Copies.RemoteCopyOf("t")!.Complete);
        Assert.False(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
    }

    [Fact]
    public async Task A_complete_copy_never_checks_again()
    {
        Seed("s", 350);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");
        var calls = _calls.Count(call => call.Session == "s");

        await _hosts.Remote.Stop();
        await _hosts.Remote.Restart();
        await Machine.ProbeAsync(default).WaitAsync(Wait);
        await SyncedAsync();

        Assert.Equal(calls, _calls.Count(call => call.Session == "s"));
        Assert.True(_hosts.MacHub.Copies.RemoteCopyOf("s")!.Complete);
    }

    // The follower copied the session forward from its start, before it was ever opened.
    private void Forward(string session, int through)
    {
        for (var seq = 1; seq <= through; seq++)
            Assert.True(_hosts.MacHub.PublishCopy(Row(session, seq, null) with { PrevSeq = seq - 1 }));
    }

    // Everything queued on the download loop before now has run: one download runs at a time, in order.
    private async Task SyncedAsync()
    {
        var other = $"sync-{Guid.NewGuid():N}";
        Seed(other, 1);
        await Machine.EnsureCopyAsync(other, default).WaitAsync(Wait);
    }
}
