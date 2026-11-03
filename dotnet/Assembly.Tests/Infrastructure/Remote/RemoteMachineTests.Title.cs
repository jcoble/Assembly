using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_remote_s_title_doesn_t_replace_a_title_the_user_gave_here()
    {
        var here = await CopyTitleAfterTurnAsync(("Mine", "user"), remoteHelperTitle: null);

        Assert.Equal(("Mine", "user"), (here.Title, here.TitleSource));
    }

    [Fact]
    public async Task A_remote_s_prompt_title_doesn_t_replace_a_helper_title_here()
    {
        var here = await CopyTitleAfterTurnAsync(("From the model", "helper"), remoteHelperTitle: null);

        Assert.Equal(("From the model", "helper"), (here.Title, here.TitleSource));
    }

    [Fact]
    public async Task A_remote_s_helper_title_replaces_a_prompt_title_here()
    {
        var here = await CopyTitleAfterTurnAsync(("Fix the build", "prompt"), remoteHelperTitle: "Build fix");

        Assert.Equal(("Build fix", "helper"), (here.Title, here.TitleSource));
    }

    // A session made on the box from here, titled here first; one turn runs on the box, which titles it by its first
    // message and then, with remoteHelperTitle, as its utility model would. Answers the session here once the refresh
    // after the turn has run.
    private async Task<SessionInfo> CopyTitleAfterTurnAsync((string Title, string Source) local, string? remoteHelperTitle)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var file = new TempDatabase();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        Assembly.Engine.Persistence.EngineSchema.Open(db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
        using var hub = new LiveHub(RowStore.Open(db));
        await using var machine = new RemoteMachine(Box, _box.Address, BoxToken, hub, sessions, _log, new MachineStatus(topics, [Box]));
        await machine.ProbeAsync(CancellationToken.None);
        var identity = await machine.ReadMirrorOwnerAsync(CancellationToken.None);
        var read = await machine.CaptureOwnerReadAsync(identity.Info.Machine, CancellationToken.None);
        var owner = identity.Binding;
        var accepted = await machine.CreateBoundSessionAsync(new("claude", Guid.NewGuid().ToString("N")), null,
            owner, read.Socket, null, CancellationToken.None);
        var session = sessions.Get(accepted.SessionId);
        Assert.NotNull(session);
        Assert.Equal(Box, session.Machine);
        Assert.NotNull(accepted.Facts);
        Assert.Equal(accepted.Facts.Agent, session.Agent);
        Assert.Equal(accepted.Facts.ParentSessionId, session.ParentSessionId);
        sessions.SetTitle(session.SessionId, local.Title, local.Source);
        // The next change to the session here is the refresh after its turn: its title, or only its time.
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topics.Subscribe(SessionsFeature.Topic, session.SessionId, _ => { }, (_, _) =>
        {
            if (sessions.Get(session.SessionId)?.Title == (local.Source == "user" || remoteHelperTitle is null ? local.Title : remoteHelperTitle))
                refreshed.TrySetResult();
        });

        machine.Watch(session);
        var clientId = Guid.NewGuid().ToString("N");
        ApiTests.AssertQueuedAcceptance(await FeatureResultAsync(_box, BoxToken, "send", "send",
            new { session = session.SessionId, text = "Fix the build", clientId }), session.SessionId, clientId);
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "title-prompt");
        Assert.Equal("Fix the build", (await SessionAsync(_box, BoxToken, session.SessionId)).GetProperty("title").GetString());
        if (remoteHelperTitle is not null)
        {
            var boxDb = (AppDatabase)typeof(Assembly.Desktop.Serve.DesktopBackend).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_box)!;
            SessionStore.Open(boxDb, TimeProvider.System, "checkouts").SetTitle(session.SessionId, remoteHelperTitle, "helper");
        }
        await agent.TextAsync("Hello");
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        return sessions.Get(session.SessionId)!;
    }
}
