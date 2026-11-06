using System.Text.Json;
using Assembly.Engine.Sessions;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class ApiTests
{
    [Fact]
    public async Task Composer_socket_discovery_returns_actual_metadata_without_a_session()
    {
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = ResultAsync("config", "discover", JsonSerializer.Serialize(new { agent = "claude", targetId, cwd = _work }));
        var agent = await _launcher.NextAsync();
        var opened = await agent.StartAsync(configOptions: "[]");
        Assert.Equal(_work, opened.GetProperty("params").GetProperty("cwd").GetString());
        var result = await pending.WaitAsync(Wait);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("options").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("modelOptions").ValueKind);
        Assert.True(agent.Disposed);
        Assert.Empty(_sessionStore.List(null, 50, default).Sessions);
    }

    [Fact]
    public async Task Composer_socket_warm_resumes_the_saved_native_identity_without_a_prompt()
    {
        var session = _database.Writer.Write("test.import-warm-session", scope =>
        {
            var id = Guid.NewGuid().ToString("D");
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            SessionImport.Import(scope, _engine.Machine, now, [new(id, null, "claude", null, _work, "", "")]);
            _sessionStore.ProjectEngineSession(scope, new(id, "claude", _work, null, null, now), null, _work);
            return _sessionStore.Get(scope, id)!;
        });
        _sessionStore.SetAcpSession(session.SessionId, "phone-restored");
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = ResultAsync("config", "warm", JsonSerializer.Serialize(new { session = session.SessionId, targetId }));
        var agent = await _launcher.NextAsync();
        var opened = await agent.StartAsync("phone-restored", configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("session/resume", opened.GetProperty("method").GetString());
        Assert.Equal("phone-restored", opened.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(_work, opened.GetProperty("params").GetProperty("cwd").GetString());
        var result = await pending.WaitAsync(Wait);
        Assert.Equal("b", result.GetProperty("options")[0].GetProperty("currentValue").GetString());
        Assert.Equal("phone-restored", _sessionStore.Get(session.SessionId)!.AcpSessionId);
        Assert.True(agent.Disposed);
    }

    [Fact]
    public async Task Composer_socket_cancel_ends_its_exact_private_discovery_link()
    {
        using var socket = await OpenOwnerSocketAsync(_base, Token);
        var targetId = _sessions.ConfigTargets[0].TargetId;
        var pending = SocketRequestAsync(socket, 1, "config", "discover", JsonSerializer.Serialize(new { agent = "claude", targetId, cwd = _work }));
        var agent = await _launcher.NextAsync();
        await agent.ReadAsync("initialize");
        await SendTextAsync(socket, """{"t":"cancel","id":1}""");
        Assert.Equal("cancelled", (await pending).GetProperty("code").GetString());
        await agent.Exited.WaitAsync(Wait);
        Assert.True(agent.Disposed);
        Assert.Empty(_sessionStore.List(null, 50, default).Sessions);
    }
}
