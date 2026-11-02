using Assembly.Tests.Engine;
using Assembly.Application.Common;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Machines;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Delivery;

public sealed partial class AdapterEffectsTests
{
    [Fact]
    public async Task Actual_local_send_waits_for_provider_insertion_while_pending_user_row_is_immediate()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var rows = RowStore.Open(db); using var hub = new LiveHub(rows);
        var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
        var session = sessions.Create("claude", "https://github.com/o/r");
        var launcher = new FakeLauncher(); await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var registry = new SessionRegistry(hub, sessions, launcher, TimeProvider.System, TextWriter.Null, work);
        var machine = new LocalMachine(hub, sessions, registry, new AttachmentStore(Path.GetTempPath()));
        var sending = machine.SendAsync(session, "exact prompt", "client", default);
        Assert.Equal("exact prompt", Assert.Single(rows.RowsAfter(session.SessionId, 0), r => r.Kind == "user").Payload.GetProperty("text").GetString());
        var agent = await launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.False(sending.IsCompleted);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.NotNull(await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        await agent.CloseAsync();
    }
}
