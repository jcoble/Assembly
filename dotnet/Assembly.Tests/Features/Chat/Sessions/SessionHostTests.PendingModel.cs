using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task Pending_raw_model_is_confirmed_before_the_first_prompt_and_consumed_once()
    {
        PendingModel("haiku");
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var set = await agent.ReadAsync("session/set_config_option");
        Assert.Equal("model", set.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("haiku", set.GetProperty("params").GetProperty("value").GetString());
        Assert.Equal("haiku", _sessionStore.Config(_session.SessionId).PendingModel);
        await agent.ReplyAsync(set, $$"""{"configOptions":{{Options("haiku", "low")}}}""");
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Null(_sessionStore.Config(_session.SessionId).PendingModel);
        Assert.Equal("haiku", _sessionStore.Config(_session.SessionId).Choices["model"]);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_unreported_model_closes_without_a_prompt_and_retains_intent(bool reportsOptions)
    {
        PendingModel("retired-wire");
        var opening = _opener.OpenAsync(_session.SessionId, "Exact draft", new(_time), CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: reportsOptions ? Options("opus", "low") : null);
        Assert.Equal(FakeLink.AcpSessionId, await agent.CloseAsync());
        var error = await Assert.ThrowsAsync<CallFailed>(() => opening.WaitAsync(Wait));
        Assert.Equal("The selected model 'retired-wire' is not available for this agent.", error.Message);
        Assert.Equal("retired-wire", _sessionStore.Config(_session.SessionId).PendingModel);
        Assert.Empty(_sessionStore.Config(_session.SessionId).Choices);
    }

    [Fact]
    public async Task A_deliberate_offered_user_choice_supersedes_pending_intent_without_an_unnecessary_set()
    {
        PendingModel("retired-wire");
        _sessionStore.SetConfigChoice(_session.SessionId, "model", "haiku");
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("haiku", "low"));
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Null(_sessionStore.Config(_session.SessionId).PendingModel);
        Assert.Equal("haiku", _sessionStore.Config(_session.SessionId).Choices["model"]);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task Local_save_failure_after_external_model_selection_preserves_the_original_intent()
    {
        var observer = PendingModel("haiku"); observer.Fail = true;
        var opening = _opener.OpenAsync(_session.SessionId, "Exact draft", new(_time), CancellationToken.None);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var set = await agent.ReadAsync("session/set_config_option");
        await agent.ReplyAsync(set, $$"""{"configOptions":{{Options("haiku", "low")}}}""");
        await agent.CloseAsync();
        await Assert.ThrowsAsync<IOException>(() => opening.WaitAsync(Wait));
        Assert.Equal("haiku", _sessionStore.Config(_session.SessionId).PendingModel);
        Assert.Empty(_sessionStore.Config(_session.SessionId).Choices);
        Assert.Null(_sessionStore.Config(_session.SessionId).Options);
    }

    [Fact]
    public async Task Adapter_IO_fallback_preserves_intent_and_the_next_open_confirms_the_saved_choice()
    {
        _host.Send("First");
        var first = await _launcher.NextAsync(); await first.StartAsync(configOptions: Options("opus", "low"));
        await first.ReadAsync("session/prompt");
        PendingModel("opus");
        var choosing = _host.SetConfigAsync("model", "haiku", CancellationToken.None);
        await first.ReadAsync("session/set_config_option"); first.Crash();
        await choosing.WaitAsync(Wait);
        Assert.Equal("opus", _sessionStore.Config(_session.SessionId).PendingModel);
        Assert.Equal("haiku", _sessionStore.Config(_session.SessionId).Choices["model"]);
        await Until(() => _host.ActiveRunId is null);
        _host.Send("Retry explicitly");
        var next = await _launcher.NextAsync(); await next.StartAsync(configOptions: Options("opus", "low"));
        var set = await next.ReadAsync("session/set_config_option");
        Assert.Equal("haiku", set.GetProperty("params").GetProperty("value").GetString());
        await next.ReplyAsync(set, $$"""{"configOptions":{{Options("haiku", "low")}}}""");
        var prompt = await next.ReadAsync("session/prompt"); Assert.Null(_sessionStore.Config(_session.SessionId).PendingModel);
        await next.FinishAsync(prompt, """{"stopReason":"end_turn"}"""); await next.CloseAsync();
    }

    private ModelObserver PendingModel(string value)
    {
        _database.Writer.Write("test.pending", scope =>
        {
            SessionStore.EnsureEngineProjectionSchema(scope);
            scope.Execute("INSERT INTO session_config(session_id,pending_model) VALUES ($id,$model) ON CONFLICT(session_id) DO UPDATE SET pending_model=excluded.pending_model", new SqlArg("$id", _session.SessionId), new SqlArg("$model", value));
            return 0;
        });
        var observer = new ModelObserver();
        _sessionStore.BindEngine(observer);
        return observer;
    }

    private sealed class ModelObserver : IEngineSessions
    {
        public bool Fail;
        public void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId) { }
        public void Titled(IWriteScope scope, string sessionId, string actualStoredTitle, ReadOnlyMemory<byte> exactTitleUtf8) { }
        public void Configured(IWriteScope scope, string sessionId, string? model) { if (Fail) throw new IOException("local observation failed"); }
        public SessionLocation? Location(string sessionId) => null;
        public SessionLocation? Location(IReadScope scope, string sessionId) => null;
        public string? Folder(string sessionId) => null;
        public string? Folder(IReadScope scope, string sessionId) => null;
    }
}
