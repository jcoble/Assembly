using System.Net;
using Assembly.Application.Common.Calls;
using Assembly.Tests.Draft.Base;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_route_that_gets_a_socket_call_error_answers_502_not_500()
    {
        await using var remote = new MachinesSocketTests.StubRemote { Reply = (feature, _) => feature == "machines" ? throw new CallFailed("boom") : null };
        await remote.StartAsync();
        await using var desktop = await DesktopOnAsync(remote, Directory.CreateTempSubdirectory("assembly-desktop-").FullName);

        using var http = Client(desktop, DesktopToken);
        var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));

        Assert.Equal(HttpStatusCode.BadGateway, created.StatusCode);
        Assert.Contains("boom", await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_helper_tools_report_remote_call_errors_and_the_outbox_logs_HTTP_errors()
    {
        var creates = 0;
        await using var remote = new MachinesSocketTests.StubRemote
        {
            Reply = (feature, _) => feature switch
            {
                "sessions" => Interlocked.Increment(ref creates) == 1 ? throw new CallFailed("boom") : """{"sessionId":"s1"}""",
                "send" => throw new CallFailed("boom"),
                _ => null
            }
        };
        await remote.StartAsync(app => app.MapGet("/outbox", () => Results.Text("outbox down", statusCode: 500)));
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        await using var desktop = await DesktopOnAsync(remote, home);
        using var http = Client(desktop, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/sessions/{parent}/send", Json("""{"text":"hi"}"""))).StatusCode);
        var agent = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent"));
        await agent.ReadAsync("session/prompt");
        var start = new { prompt = "Count the lines.", agent = "codex", machine = Box };

        Assert.Contains("boom", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Contains("boom", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Contains("boom", MessageError(Path.Combine(home, "assembly.db"), "s1"));
        Assert.Contains("boom", await ToolErrorAsync(desktop, parentToken, "send_message", new { sessionId = "s1", text = "Which file?" }));

        // s1 is a helper on the remote, so the outbox looks there, and logs the 500 instead of dying.
        await Until(() => _log.ToString().Contains($"{Box} outbox: {Box} answered 500: outbox down"), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_remote_that_drops_its_welcome_does_not_fault_the_follower()
    {
        await using var remote = new MachinesSocketTests.StubRemote
        {
            Reply = (feature, _) =>
            feature == "sessions" ? """{"sessionId":"s1"}""" : null
        };
        await remote.StartAsync();
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        await using (var first = await DesktopOnAsync(remote, home))
        {
            using var http = Client(first, DesktopToken);
            var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        // A new desktop checks the format again, when opening s1 starts following it.
        remote.DropVersion = true;
        await using var desktop = await DesktopOnAsync(remote, home);
        await GetJsonAsync(desktop, DesktopToken, "/sessions/s1/rows");

        await Until(() => _log.ToString().Contains($"remote session s1: {Box} didn't answer"));
    }
}
