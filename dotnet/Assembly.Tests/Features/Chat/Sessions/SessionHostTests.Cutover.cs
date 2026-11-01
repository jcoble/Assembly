using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Existing_untitled_owner_first_prompt_uses_the_same_exact_chat_and_Engine_title()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        Assert.Null(_sessionStore.Get(_session.SessionId)!.Title);
        _host.Send("  Exact first title\nBody stays exact");
        var agent = await _launcher.NextAsync(); await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("  Exact first title\nBody stays exact", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal("Exact first title", _sessionStore.Get(_session.SessionId)!.Title);
        Assert.Equal("Exact first title", _database.QueryFirst("SELECT title FROM engine_session WHERE id=$id", r => r.Text(0), default, new SqlArg("$id", _session.SessionId)));
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
    }

    [Fact]
    public async Task Engine_first_empty_owner_root_projects_null_and_keeps_existing_first_prompt_naming()
    {
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        var folder = Directory.CreateTempSubdirectory("assembly-cutover-title-").FullName;
        var context = new CommandContext(Guid.NewGuid(), runtime.Machine, new(ActorKind.Window, "owner"), "engine-first", 0, 0, 1_000_000);
        Assert.True(runtime.Submit(context, new StartSession("claude", null, folder, "", null, null, null)).Applied);
        Assert.Null(_sessionStore.Get("engine-first")!.Title);
        var host = new SessionHost("engine-first", _hub, _sessionStore, _opener, _time, _log, _work);
        try
        {
            host.Send("New exact title");
            var agent = await _launcher.NextAsync(); await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            Assert.Equal("New exact title", _sessionStore.Get("engine-first")!.Title);
            Assert.Equal("New exact title", _database.QueryFirst("SELECT title FROM engine_session WHERE id='engine-first'", r => r.Text(0), default));
            await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await agent.CloseAsync();
        }
        finally
        {
            var stopped = host.StopForShutdownAsync();
            await _adapter.DisposeAsync();
            await stopped;
            Directory.Delete(folder, true);
        }
    }
}
