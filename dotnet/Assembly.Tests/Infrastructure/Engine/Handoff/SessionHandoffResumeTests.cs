using Assembly.Application.Common;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class SessionHandoffResumeTests
{
    [Theory]
    [InlineData(-32601)]
    [InlineData(-32000)]
    [InlineData(-32603)]
    public async Task Strict_resume_preserves_the_typed_refusal_and_disposes_without_starting_a_new_conversation(int code)
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var folder = Path.Combine(Path.GetTempPath(), "handoff-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var time = new FakeTimeProvider(); var launcher = new FakeLauncher();
            var sessions = SessionStore.Open(db, time, "checkouts");
            var session = sessions.Create("claude", "repo", workingFolder: folder);
            sessions.SetAcpSession(session.SessionId, "retained-public-acp");
            await using var pool = new SharedAdapter(launcher, "claude", time, TextWriter.Null);
            var opener = new SessionOpener(sessions, launcher, _ => pool);
            var resuming = opener.ResumeForHandoffAsync(session.SessionId, "retained-public-acp", new StageTimes(time), default, () => { });
            var agent = await launcher.NextAsync(); await SessionWarmConfigTests.Initialize(agent);
            var request = await agent.ReadAsync("session/resume");
            Assert.Equal("retained-public-acp", request.GetProperty("params").GetProperty("sessionId").GetString());
            Assert.False(request.GetProperty("params").TryGetProperty("replayFrom", out _));
            await agent.ReplyErrorAsync(request, code, "PRIVATE_PROVIDER_DETAIL");

            var error = await Assert.ThrowsAsync<JsonRpcException>(() => resuming.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(code, error.Code);
            Assert.True(agent.Disposed);
            Assert.Equal(1, launcher.Launches);
            Assert.Equal("retained-public-acp", sessions.Get(session.SessionId)!.AcpSessionId);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Mismatched_public_identity_is_refused_before_any_adapter_launch()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var time = new FakeTimeProvider(); var launcher = new FakeLauncher();
        var sessions = SessionStore.Open(db, time, "checkouts");
        var session = sessions.Create("claude", "repo"); sessions.SetAcpSession(session.SessionId, "public-A");
        await using var pool = new SharedAdapter(launcher, "claude", time, TextWriter.Null);
        var opener = new SessionOpener(sessions, launcher, _ => pool);

        await Assert.ThrowsAsync<CallFailed>(() => opener.ResumeForHandoffAsync(session.SessionId, "native-B-is-not-public-A",
            new StageTimes(time), default, () => { }));

        Assert.Equal(0, launcher.Launches);
    }
}
