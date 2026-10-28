using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_remote_s_title_doesn_t_replace_a_title_the_user_gave_here()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var file = new TempDatabase();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var sessions = SessionStore.Open(db, TimeProvider.System);
        using var hub = new LiveHub(RowStore.Open(db));
        await using var machine = new RemoteMachine(Box, _box.Address, BoxToken, hub, sessions, _log, new MachineStatus(topics, [Box]));
        var session = await machine.CreateSessionAsync("claude", "https://github.com/o/r", CancellationToken.None);
        sessions.SetTitle(session.SessionId, "Mine", "user");
        // The next change to the session here is the refresh after its turn: its title, or only its time.
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topics.Subscribe(SessionsFeature.Topic, session.SessionId, _ => { }, (_, _) => refreshed.TrySetResult());

        await machine.SendAsync(session, "Fix the build", null, CancellationToken.None);
        await RunTurnOnBoxAsync();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Fix the build", (await GetJsonAsync(_box, BoxToken, $"/sessions/{session.SessionId}")).GetProperty("title").GetString());
        var here = sessions.Get(session.SessionId)!;
        Assert.Equal(("Mine", "user"), (here.Title, here.TitleSource));
    }
}
