using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Sessions.Drafts;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

// The old app's remote drafts (agent_conversation/mod.rs:172-208, remote/server/mod.rs:426-436): a remote session's
// unsent draft is saved in the remote machine's own database, read back with the session's open, and its send clears it.
public sealed partial class ChatExpectedSourceTests
{
    [Fact]
    public async Task A_remote_sessions_draft_is_saved_on_the_remote_comes_back_with_its_open_and_its_send_clears_it()
    {
        await using var f = await RemoteFixture.Start(); using var socket = await Open(f.Hosts.Mac.Address);
        var draft = new SessionDraft("half a remote thought", [new DraftAttachment("shot.png", 2048, "/remote/shot.png")]);
        Result(await Call(socket, 1, "drafts", "set", DraftsFeature.Args("selected", draft, f.Source)));
        Assert.Equal(1, f.Hosts.Remote.Fixture.Count("session_draft"));
        Assert.Equal(0, f.Hosts.Mac.Fixture.Count("session_draft"));

        var opened = OpenedDraft(Result(await Call(socket, 2, "sessions", "open_target", OpenArgs(f))));
        Assert.Equal(draft.Text, opened.Text); Assert.Equal(draft.Attachments, opened.Attachments);

        Result(await Call(socket, 3, "send", "send", Args(new SendFeature.SendArgs("selected", draft.Text, "client", ExpectedSource: f.Source), SessionsJson.Default.SendArgs)));
        Assert.Equal(0, f.Hosts.Remote.Fixture.Count("session_draft"));
        Assert.Equal("", OpenedDraft(Result(await Call(socket, 4, "sessions", "open_target", OpenArgs(f)))).Text);
    }

    [Fact]
    public async Task A_local_sessions_draft_saves_through_its_source_and_a_stale_source_saves_nothing()
    {
        await using var hosts = new TwoHosts(windowCalls: true); Installed? installed = null;
        hosts.Mac.ChatDoor = new DeferredReads(() => installed?.Door);
        hosts.Mac.AdditionalFeatures = calls => installed = Install(hosts.Mac, calls);
        await hosts.Start(); Seed(hosts.Mac, "selected"); using var socket = await Open(hosts.Mac.Address);
        var source = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        Error(await Call(socket, 1, "drafts", "set", DraftsFeature.Args("selected", new("stale", []), source with { Machine = "elsewhere" })), "source_changed");
        Assert.Equal(0, hosts.Mac.Fixture.Count("session_draft"));
        Result(await Call(socket, 2, "drafts", "set", DraftsFeature.Args("selected", new("kept here", []), source)));
        Assert.Equal(1, hosts.Mac.Fixture.Count("session_draft"));
    }

    private static JsonElement OpenArgs(RemoteFixture f) =>
        Args(new SessionOpenTarget("engine", f.Source.Machine, "selected"), SessionOpenJson.Default.SessionOpenTarget);

    private static SessionDraft OpenedDraft(JsonElement read) =>
        read.Deserialize(SessionOpenJson.Default.SessionOpenTargetRead)!.Draft ?? throw new InvalidOperationException("the open carries the remote's draft");
}
