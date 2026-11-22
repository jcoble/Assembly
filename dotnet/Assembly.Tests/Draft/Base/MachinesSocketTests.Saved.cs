using System.Text.Json;

namespace Assembly.Tests.Draft.Base;

public sealed partial class MachinesSocketTests
{
    [Fact]
    public async Task Subscription_and_status_publication_do_not_wait_for_the_probe_lock()
    {
        var probes = typeof(Assembly.Application.Features.Chat.Sessions.MachinesFeature)
            .GetField("_probes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_feature)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holding = Task.Run(() => { lock (probes) { held.Set(); release.Wait(); } });
        held.Wait();
        using var socket = await OpenAsync();
        var subscription = SubAsync(socket, 1, "machines");
        var tick = Task.Run(() => _feature.Tick(true));
        var publication = Task.Run(() => _status.Reachable("box"));
        try
        {
            await subscription.WaitAsync(Wait);
            await publication.WaitAsync(Wait);
        }
        finally { release.Set(); await holding; }
        await tick.WaitAsync(Wait);
    }

    [Fact]
    public async Task Stopping_a_machine_cancels_and_awaits_its_hanging_probe()
    {
        _stub.HangVersion = true;
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        _time.Advance(TimeSpan.FromSeconds(1));
        await _stub.VersionStarted.Task.WaitAsync(Wait);
        await _feature.StopProbesAsync("box");
        await _stub.VersionCancelled.Task.WaitAsync(Wait);
        Assert.Equal(0, _time.Timers - 1);
    }

    [Fact]
    public async Task Socket_connect_and_disconnect_keep_the_saved_defaults_and_reject_remote_calls_until_reconnect()
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "set", """{"name":"saved","ssh":"host"}""");
        var saved = await ReadAsync(socket);
        Assert.Contains("\"port\":9797", saved);
        Assert.Contains("\"tokenFile\":\".config/assembly-host/env\"", saved);
        await CallAsync(socket, 2, "machines", "connect", """{"machine":"saved"}""");
        Assert.Contains("\"reachable\":true", await ReadAsync(socket));
        await CallAsync(socket, 3, "machines", "disconnect", """{"name":"saved"}""");
        Assert.Contains("disconnected", await ReadAsync(socket));
        await CallAsync(socket, 4, "machines", "agents", """{"machine":"saved"}""");
        Assert.Contains("unknown machine", await ReadAsync(socket));
        using var state = JsonDocument.Parse(await SubAsync(socket, 5, "machines"));
        var item = state.RootElement.GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("key").GetString() == "saved").GetProperty("item");
        Assert.Equal("host", item.GetProperty("ssh").GetString());
        Assert.Equal("disconnected", item.GetProperty("reason").GetString());
        await UnsubAsync(socket, 5);
        await CallAsync(socket, 6, "machines", "connect", """{"machine":"saved"}""");
        Assert.Contains("\"reachable\":true", await ReadAsync(socket));
    }
}
