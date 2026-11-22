namespace Assembly.Tests.Infrastructure.Socket;

// Moving a remote session this desktop did not create from the rail (working, done, settled) before it was ever opened.
public sealed partial class ChatExpectedSourceTests
{
    [Fact]
    public async Task Moving_a_session_only_the_mirror_carries_from_the_rail_records_its_facade_and_moves_it()
    {
        await using var f = await RemoteFixture.Start(recorded: false); using var socket = await Open(f.Hosts.Mac.Address);
        Assert.Null(f.Hosts.Mac.Sessions.Get("selected"));
        Result(await Call(socket, 1, "sessions", "mark", Element("""{"session":"selected","state":"settled"}""")));
        AssertFacade(f, "selected", parent: null);
        Assert.Equal(1, f.Hosts.Mac.Fixture.Number(
            "SELECT COUNT(*) FROM session_info WHERE session_id='selected' AND settled_at IS NOT NULL"));
        // A later open finds it recorded and writes nothing more.
        var moved = Recorded(f);
        Result(await Call(socket, 2, "sessions", "open_target", OpenArgs(f)));
        Assert.Equal(moved, Recorded(f));
    }

    [Fact]
    public async Task Moving_a_session_whose_owner_cant_be_proven_writes_nothing_and_is_refused_as_before()
    {
        await using var f = await RemoteFixture.Start(recorded: false);
        // No connection serves the mirror now, so its owner can't be proven here.
        await f.Hosts.DisconnectMirror();
        var before = Recorded(f);
        using var socket = await Open(f.Hosts.Mac.Address);
        var refused = await Call(socket, 1, "sessions", "mark", Element("""{"session":"selected","state":"done"}"""));
        Assert.Equal("error", refused.GetProperty("t").GetString());
        Assert.Contains("unknown session", refused.GetRawText());
        Assert.Null(f.Hosts.Mac.Sessions.Get("selected"));
        Assert.Equal(before, Recorded(f));
    }
}
