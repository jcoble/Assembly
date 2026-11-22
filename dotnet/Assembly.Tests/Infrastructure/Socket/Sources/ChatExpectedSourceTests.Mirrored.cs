using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Sessions.Drafts;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

// A remote session this desktop did not create (started on the remote, by its workflows or phone, or by another desktop)
// arrives only through the remote's mirror. Its first open records its facade, so it is then used as a created one is.
public sealed partial class ChatExpectedSourceTests
{
    [Fact]
    public async Task A_session_only_the_mirror_carries_is_read_sent_to_configured_and_listed_once_from_its_first_open()
    {
        await using var f = await RemoteFixture.Start(recorded: false); using var socket = await Open(f.Hosts.Mac.Address);
        Assert.Null(f.Hosts.Mac.Sessions.Get("selected"));
        Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        AssertFacade(f, "selected", parent: null);

        using (var bulk = await Open(f.Hosts.Mac.Address, bulk: true))
            Result(await Call(bulk, 2, "rows", "page", PageArgs("selected", f.Source)));
        Assert.Contains("OWNER", Result(await Call(socket, 3, "config", "get",
            Args(new ConfigFeature.GetArgs("selected", f.Source), SessionsJson.Default.GetArgs))).GetRawText());
        var accepted = Result(await Call(socket, 4, "send", "send",
            Args(new SendFeature.SendArgs("selected", "From a desktop that never made it", "client", ExpectedSource: f.Source), SessionsJson.Default.SendArgs)));
        Assert.Equal("queued", accepted.GetProperty("state").GetString());
        Assert.Equal(1, f.Hosts.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_message WHERE to_session_id='selected'"));

        var rail = Result(await Call(socket, 5, "sessions", "rail",
            Element("""{"locations":["remote"],"groupBy":{"status":false,"project":false},"limit":200}""")));
        var listed = Assert.Single(rail.GetProperty("sessions").EnumerateArray(), row => row.GetProperty("kind").GetString() == "session" &&
            row.GetProperty("sessionId").GetString() == "selected");
        Assert.Equal(f.Source.Machine, listed.GetProperty("source").GetProperty("machine").GetString());
    }

    [Fact]
    public async Task A_draft_typed_after_the_first_open_of_a_session_only_the_mirror_carries_is_saved_on_the_remote_and_comes_back_with_its_next_open()
    {
        await using var f = await RemoteFixture.Start(recorded: false); using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        var draft = new SessionDraft("typed on a desktop that never made it", []);
        Result(await Call(socket, 2, "drafts", "set", DraftsFeature.Args("selected", draft, f.Source)));
        Assert.Equal(1, f.Hosts.Remote.Fixture.Count("session_draft"));
        Assert.Equal(0, f.Hosts.Mac.Fixture.Count("session_draft"));
        Assert.Equal(draft.Text, OpenedDraft(Result(await Call(socket, 3, "sessions", "open_target", OpenArgs(f)))).Text);
    }

    [Fact]
    public async Task Opening_a_session_only_the_mirror_carries_again_writes_nothing()
    {
        await using var f = await RemoteFixture.Start(recorded: false); using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        var first = Recorded(f);
        Result(await Call(socket, 2, "sessions", "open_target", OpenArgs(f)));
        Assert.Equal(first, Recorded(f));
    }

    [Fact]
    public async Task The_first_open_of_a_session_only_the_mirror_carries_shows_its_helper_cards()
    {
        await using var f = await RemoteFixture.Start(recorded: false, seedRemote: SeedHelper, helperCards: true);
        // The mirror alone shows no helper: its parent has no facade here yet.
        Assert.Null(f.Hosts.Mac.Sessions.Get("helper"));
        using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        var card = f.Hosts.Mac.Sessions.Get("helper") ?? throw new InvalidOperationException("no helper card on the first open");
        Assert.True(card.Helper); Assert.Equal("selected", card.ParentSessionId); Assert.Equal("workbox-test", card.Machine);
    }

    [Fact]
    public async Task Opening_a_helper_of_a_session_only_the_mirror_carries_records_its_facade_under_its_parent()
    {
        await using var f = await RemoteFixture.Start(recorded: false, seedRemote: SeedHelper, helperCards: true);
        using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 1, "sessions", "open_target",
            Args(new SessionOpenTarget("engine", f.Source.Machine, "helper"), SessionOpenJson.Default.SessionOpenTarget)));
        AssertFacade(f, "selected", parent: null);
        AssertFacade(f, "helper", parent: "selected");
        Result(await Call(socket, 2, "config", "get", Args(new ConfigFeature.GetArgs("helper", f.Source), SessionsJson.Default.GetArgs)));
    }

    [Fact]
    public async Task A_replaced_engine_leaves_an_opened_facade_with_an_unknown_owner_that_a_later_open_does_not_change()
    {
        await using var f = await RemoteFixture.Start(recorded: false);
        var original = f.Source;
        using (var socket = await Open(f.Hosts.Mac.Address))
            Result(await Call(socket, 1, "sessions", "open_target", OpenArgs(f)));
        await f.Hosts.DisconnectMirror();
        var replacement = new TwoHosts.Host(windowCalls: true);
        await f.Hosts.ReplaceAtSameEndpoint(replacement);
        await f.Hosts.ReconnectSaved();
        await f.Hosts.Reached(f.Hosts.Remote.Through);
        var current = f.Hosts.Mac.Fixture.Db.QueryFirst("""
            SELECT machine,generation,name FROM engine_mirror WHERE name='workbox-test' AND state='live'
            """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
        Assert.NotEqual(original.Machine, current.Machine);

        using var after = await Open(f.Hosts.Mac.Address);
        var reopened = await Call(after, 1, "sessions", "open_target",
            Args(new SessionOpenTarget("engine", current.Machine, "selected"), SessionOpenJson.Default.SessionOpenTarget));
        Assert.Equal("error", reopened.GetProperty("t").GetString());
        // Kept, not deleted: a saved copy of its rows still has its owner record. It names the retired engine.
        Assert.Equal("workbox-test", f.Hosts.Mac.Sessions.Get("selected")!.Machine);
        Assert.Equal(original.Machine, f.Hosts.Mac.Fixture.Text("SELECT machine FROM engine_session WHERE id='selected'"));
        var refused = await Call(after, 2, "send", "send",
            Args(new SendFeature.SendArgs("selected", "To a reused name", "client", ExpectedSource: current), SessionsJson.Default.SendArgs));
        Error(refused, "source_changed");
        Assert.Equal(0, f.Hosts.Remote.Fixture.Count("engine_message"));
    }

    private static void SeedHelper(TwoHosts.Host remote)
    {
        remote.Sessions.Add("helper", "codex", "source-derived", null!);
        remote.Fixture.SeedSession("helper", state: "idle", parentId: "selected");
    }

    private static void AssertFacade(RemoteFixture f, string session, string? parent)
    {
        var facade = f.Hosts.Mac.Sessions.Get(session) ?? throw new InvalidOperationException($"no facade for {session}");
        Assert.Equal("workbox-test", facade.Machine); Assert.Equal("codex", facade.Agent);
        Assert.Equal(parent, facade.ParentSessionId); Assert.Equal(parent is not null, facade.Helper);
        Assert.Equal($"foreign|{f.Source.Machine}", f.Hosts.Mac.Fixture.Text(
            "SELECT state||'|'||machine FROM engine_session WHERE id=$id AND holder=machine", new SqlArg("$id", session)));
    }

    // Everything a first open writes: the facades, their owner records and the ancestry receipts.
    private static string Recorded(RemoteFixture f) => f.Hosts.Mac.Fixture.Text("""
        SELECT (SELECT group_concat(session_id||'@'||updated_at) FROM session_info)||'|'||
            (SELECT COUNT(*) FROM engine_session)||'|'||(SELECT COUNT(*) FROM engine_command_receipt)
        """)!;

    private static JsonElement PageArgs(string session, ExpectedSource source) => Element(
        $$"""{"session":"{{session}}","expectedSource":{{JsonSerializer.Serialize(source, Assembly.Application.Features.Chat.Rows.RowJsonContext.Rows.ExpectedSource)}}}""");
}
