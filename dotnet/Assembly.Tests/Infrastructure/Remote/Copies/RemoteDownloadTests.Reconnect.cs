using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Remote.Copies;

/// <summary>
/// What a reconnect does with the saved copies (TSK-1692): one <c>rows/heads</c> call, then only the sessions that
/// changed are caught up, downloaded again or backfilled.
/// </summary>
public sealed partial class RemoteDownloadTests
{
    // Pins: spec test 10.
    [Fact]
    public async Task Spec10_a_reconnect_with_three_saved_sessions_one_changed_catches_up_only_that_one()
    {
        foreach (var session in new[] { "a", "b", "c" })
        {
            // Over one first chunk, so each download reaches the bottom with a backfill call.
            Seed(session, 150);
            await Machine.EnsureCopyAsync(session, default).WaitAsync(Wait);
            await DownloadedAsync(session);
        }
        using var viewer = _hosts.MacHub.Subscribe("c", _hosts.MacHub.LastSeq("c"));
        _calls.Clear();

        await ReconnectAsync(() =>
        {
            for (var seq = 151; seq <= 155; seq++) Save("c", seq, null);
        });
        for (var seq = 151; seq <= 155; seq++)
            Assert.Equal(seq, (await viewer.Queue.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Seq);

        Assert.Equal(RemoteRows("c"), MacRows("c"));
        Assert.Single(_calls, call => call.Method == "heads");
        Assert.DoesNotContain(_calls, call => call.Session is "a" or "b");
        Assert.DoesNotContain(_calls, call => call.Method == "before" && call.Session == "c");
    }

    // Pins: spec test 15.
    [Fact]
    public async Task Spec15_a_head_below_the_copy_drops_and_downloads_again()
    {
        Seed("s", 350);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");
        _bottomed.TryRemove("s", out _);

        await ReconnectAsync(() => Shrink("s", 300));
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(300, _hosts.MacHub.LastSeq("s"));
    }

    // Pins: spec test 16.
    [Fact]
    public async Task Spec16_rows_imported_below_the_copy_are_backfilled()
    {
        Seed("s", 350);
        // The oldest 50 rows are set aside under another session until the remote imports them.
        Remote("UPDATE rows SET session_id='s-imported' WHERE session_id='s' AND seq<=50");
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");
        Assert.Equal(51, _hosts.MacHub.Copies.FirstSeq("s"));
        _bottomed.TryRemove("s", out _);

        await ReconnectAsync(() => Remote("UPDATE rows SET session_id='s' WHERE session_id='s-imported'"));
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(1, _hosts.MacHub.Copies.FirstSeq("s"));
    }

    // Pins: spec test 17: a forward copy adopted at startup, with no head yet, is decided by the reconnect's one call.
    [Fact]
    public async Task Spec17_an_adopted_copy_ends_complete_with_the_same_rows()
    {
        Seed("s", 350);
        Forward("s", 350);
        Mac($"INSERT INTO remote_copy(session_id,machine,state,head) VALUES('s','{_hosts.Remote.Runtime.Machine}','downloading',NULL)");
        _calls.Clear();

        await ReconnectAsync(() => { });
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Single(_calls, call => call.Method == "heads");
        Assert.DoesNotContain(_calls, call => call.Method == "after" && call.Session == "s");
    }

    // Pins: spec test 14.
    [Fact]
    public async Task Spec14_a_different_engine_under_the_same_name_drops_the_copy()
    {
        Seed("s", 150);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");
        // The copy was saved from the engine this name pointed at before, and differs from the one there now.
        Mac("UPDATE remote_copy SET machine='an-older-engine' WHERE session_id='s'");
        Mac("UPDATE rows SET payload='{\"text\":\"from the older engine\"}' WHERE session_id='s' AND seq=5");
        using var viewer = _hosts.MacHub.Subscribe("s", _hosts.MacHub.LastSeq("s"));
        _bottomed.TryRemove("s", out _);

        await ReconnectAsync(() => { });
        await DownloadedAsync("s");

        Assert.Equal(RemoteRows("s"), MacRows("s"));
        Assert.Equal(_hosts.Remote.Runtime.Machine, _hosts.MacHub.Copies.RemoteCopyOf("s")!.Machine);
    }

    [Fact]
    public async Task A_drop_cancels_the_session_s_follower_and_download_first()
    {
        Seed("s", 350);
        await Machine.EnsureCopyAsync("s", default).WaitAsync(Wait);
        await DownloadedAsync("s");
        using var viewer = _hosts.MacHub.Subscribe("s", _hosts.MacHub.LastSeq("s"));
        Machine.WatchRun("s", "a-run-still-to-come");
        Assert.Contains("s", Machine.FollowedSessions);
        _bottomed.TryRemove("s", out _);

        await ReconnectAsync(() => Shrink("s", 300));
        await DownloadedAsync("s");

        Assert.DoesNotContain("s", Machine.FollowedSessions);
        Assert.Equal(RemoteRows("s"), MacRows("s"));
    }

    // The remote goes away and comes back; whileAway changes its rows before this machine connects again. Returns once
    // the reconnect's pass over the saved copies has run.
    private async Task ReconnectAsync(Action whileAway)
    {
        await _hosts.Remote.Stop();
        await _hosts.Remote.Restart();
        whileAway();
        await Machine.ProbeAsync(default).WaitAsync(Wait);
        await SyncedAsync();
    }

    // The remote's history now ends at seq last, as after a rebuild.
    private void Shrink(string session, long last)
    {
        Remote($"DELETE FROM rows WHERE session_id='{session}' AND seq>{last}");
        Remote($"UPDATE sessions SET next_seq={last + 1} WHERE session_id='{session}'");
    }

    private void Remote(string sql) => Write(_hosts.Remote.Fixture.Db, sql);

    private void Mac(string sql) => Write(_hosts.Mac.Fixture.Db, sql);

    private static void Write(AppDatabase db, string sql) => db.Writer.Write("test.edit", scope => scope.Execute(sql));
}
