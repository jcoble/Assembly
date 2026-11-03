using System.Net.Http.Headers;
using System.Text;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Actual_host_shutdown_joins_blocked_sdk_need_and_finishes_its_audit_before_database_disposal(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-need-host-stop-").FullName;
        var launcher = new FakeLauncher();
        Host? running = null;
        try
        {
            running = await Host.Start(host, home, null, launcher: launcher);
            using var socket = await running.Open();
            await Command(socket, "parent", new StartSession("codex", null, home, "Parent", null, null, null));
            await running.WaitState("sessions", "parent", "idle");
            await Command(socket, "prompt", new SendMessage(null, "parent", "Work", "prompt"));
            var agent = await launcher.NextAsync();
            var opening = await agent.StartAsync();
            var bearer = opening.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0]
                .GetProperty("value").GetString()!["Bearer ".Length..];
            await agent.ReadAsync("session/prompt");
            // Retained disconnected profile makes the owning relay's target census incomplete.
            // This isolated fixture write does not publish a profile-change command or start SSH.
            ((IEngineRuntime)running.Runtime).Write(scope => scope.Execute("""
                CREATE TABLE IF NOT EXISTS saved_machines(name TEXT PRIMARY KEY,ssh TEXT NOT NULL,port INTEGER NOT NULL,token_file TEXT NOT NULL);
                INSERT INTO saved_machines(name,ssh,port,token_file) VALUES('workbox-test','workbox-test',9887,'unused-test-token');
                """));
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscribed = running.Runtime.Publisher.Register(item =>
            {
                if (item.Kind == "parent_need.requested") accepted.TrySetResult();
            }, out _);
            using var http = new HttpClient { BaseAddress = running.Address };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"answer_need\",\"arguments\":{\"needId\":\"remote-need\",\"answer\":\"yes\"}}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            var pending = http.SendAsync(request);
            await accepted.Task.WaitAsync(Timeout);
            Assert.False(pending.IsCompleted);
            await running.DisposeAsync().AsTask().WaitAsync(Timeout);
            running = null;
            try { using var response = await pending.WaitAsync(Timeout); }
            catch (HttpRequestException) { }
            using var db = TestDatabase.Open(Path.Combine(home, "assembly.db"));
            Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM engine_tool_call WHERE tool='answer_need' AND outcome='failed' AND ended_ms IS NOT NULL",
                row => row.Long(0), default));
            Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND state='pending'",
                row => row.Long(0), default));
        }
        finally
        {
            if (running is not null) await running.DisposeAsync();
            Directory.Delete(home, true);
        }
    }
}
