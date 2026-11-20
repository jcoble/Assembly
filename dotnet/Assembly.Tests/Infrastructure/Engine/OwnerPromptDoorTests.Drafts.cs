using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions.Drafts;
using Assembly.Infrastructure.Engine.Sessions;

namespace Assembly.Tests.Infrastructure.Engine;

// The old app's session_draft_survives_manager_restart_and_clears: a local session's unsent draft is kept on its
// machine, comes back with the session's open after a restart, and a send clears it.
public sealed partial class OwnerPromptDoorTests
{
    [Fact]
    public async Task A_local_draft_survives_a_backend_restart_and_comes_back_with_its_open()
    {
        await using var h = new Harness();
        var id = h.Sessions.Create("codex", "repo").SessionId;
        h.Fixture.SeedSession(id);
        var draft = new SessionDraft("half a thought", [new DraftAttachment("shot.png", 2048, "/saved/shot.png")]);
        h.Fixture.Db.Writer.Write("drafts/set", scope => { DraftStore.Set(scope, id, draft, 1); return 0; });

        await h.Reopen();

        var saved = OpenDraft(h, id);
        Assert.Equal(draft.Text, saved.Text);
        Assert.Equal(draft.Attachments, saved.Attachments);
    }

    [Fact]
    public async Task A_send_clears_its_sessions_draft_with_its_accept_and_the_clear_outlasts_a_restart()
    {
        await using var h = new Harness();
        var id = h.Sessions.Create("codex", "repo").SessionId;
        h.Fixture.SeedSession(id);
        h.Fixture.Db.Writer.Write("drafts/set", scope => { DraftStore.Set(scope, id, new("sent soon", []), 1); return 0; });

        h.Fixture.Db.Writer.Write("send/send", await h.Door.PrepareOwnerPromptAsync(new OwnerPromptRequest(id, "sent soon", "C", [], null), default));
        await h.Reopen();

        Assert.Equal(SessionDraft.Empty.Text, OpenDraft(h, id).Text);
        Assert.Empty(OpenDraft(h, id).Attachments);
    }

    private static SessionDraft OpenDraft(Harness h, string id) =>
        new SessionOpenTargets(h.Fixture.Db, h.Sessions)
            .Validate(new("engine", h.Fixture.Machine, id), new ExpectedSource(h.Fixture.Machine, 0, null), default)
            .Draft ?? throw new InvalidOperationException("a local engine open carries its draft");
}
