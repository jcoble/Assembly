namespace Assembly.Tests.Infrastructure.Socket;

// The first open of a remote session this desktop did not create shows the title the remote already gave it.
public sealed partial class ChatExpectedSourceTests
{
    [Fact]
    public async Task The_first_open_of_a_session_only_the_mirror_carries_records_its_title_for_the_pane()
    {
        await using var f = await RemoteFixture.Start(recorded: false); using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        Assert.Equal("Test", f.Hosts.Mac.Sessions.Get("selected")!.Title);
    }
}
