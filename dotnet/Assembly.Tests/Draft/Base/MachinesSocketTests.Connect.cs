namespace Assembly.Tests.Draft.Base;

/// <summary>
/// <c>machines/connect</c>, the window's Connect button for an offline remote: it probes now, again every half second
/// while nothing answers, for up to 10 seconds, and answers the machine's item.
/// </summary>
public sealed partial class MachinesSocketTests
{
    // Pins: sessions-list#12
    [Fact]
    public async Task Connect_probes_the_remote_now_and_again_while_nothing_answers()
    {
        _stub.DropVersion = true;
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "connect", """{"machine":"box"}""");
        await Until(() => _stub.VersionHits == 1);

        // Its 10 s limit and its half-second wait.
        await Until(() => _time.Timers == 2);
        _stub.DropVersion = false;
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal($$"""{"t":"result","id":1,"result":{{Box(true, null)}}}""", await ReadAsync(socket));
        Assert.Equal(2, _stub.VersionHits);
    }

    // Pins: sessions-list#12
    [Fact]
    public async Task Connect_gives_up_after_10_seconds_and_answers_the_machine_still_offline()
    {
        _stub.DropVersion = true;
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "connect", """{"machine":"box"}""");
        await Until(() => _time.Timers == 2);

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal($$"""{"t":"result","id":1,"result":{{Box(false, "didn't answer")}}}""", await ReadAsync(socket));
    }
}
