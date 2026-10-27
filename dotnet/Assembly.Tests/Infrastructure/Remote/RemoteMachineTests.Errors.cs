using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_route_that_gets_a_non_2xx_from_the_remote_answers_502_not_500()
    {
        await using var remote = await StubAsync(app => app.MapGet("/agents", () => Results.Text("boom", statusCode: 500)));
        await using var desktop = await DesktopOnAsync(remote, Directory.CreateTempSubdirectory("assembly-desktop-").FullName);

        using var http = Client(desktop, DesktopToken);
        var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));

        Assert.Equal(HttpStatusCode.BadGateway, created.StatusCode);
        Assert.Contains("answered 500", await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_helper_tools_answer_a_500_from_the_remote_as_a_tool_error_and_the_outbox_logs_it()
    {
        var creates = 0;
        await using var remote = await StubAsync(app =>
        {
            // The first create fails; the next makes s1, whose sends fail.
            app.MapPost("/sessions", () => Interlocked.Increment(ref creates) == 1
                ? Results.Text("boom", statusCode: 500)
                : Results.Text("""{"sessionId":"s1"}""", "application/json"));
            app.MapPost("/sessions/s1/send", () => Results.Text("boom", statusCode: 500));
            app.MapGet("/outbox", () => Results.Text("outbox down", statusCode: 500));
        });
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

        Assert.Contains("answered 500", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Contains("answered 500", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Contains("answered 500", MessageError(Path.Combine(home, "assembly.db"), "s1"));
        Assert.Contains("answered 500", await ToolErrorAsync(desktop, parentToken, "send_message", new { sessionId = "s1", text = "Which file?" }));

        // s1 is a helper on the remote, so the outbox looks there, and logs the 500 instead of dying.
        await Until(() => _log.ToString().Contains($"{Box} outbox: {Box} answered 500: outbox down"), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_remote_that_answers_500_to_version_does_not_fault_the_follower()
    {
        var versionStatus = 200;
        await using var remote = await StubAsync(app =>
        {
            app.MapGet("/agents", () => Results.Text("""{"agents":[{"id":"claude","name":"Claude"}]}""", "application/json"));
            app.MapPost("/sessions", () => Results.Text("""{"sessionId":"s1"}""", "application/json"));
        }, () => versionStatus);
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        await using (var first = await DesktopOnAsync(remote, home))
        {
            using var http = Client(first, DesktopToken);
            var created = await http.PostAsync("/sessions", Json($$"""{"agent":"claude","repoUrl":"https://github.com/o/r","machine":"{{Box}}"}"""));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        // A new desktop checks the format again, when opening s1 starts following it.
        versionStatus = 500;
        await using var desktop = await DesktopOnAsync(remote, home);
        await GetJsonAsync(desktop, DesktopToken, "/sessions/s1/rows");

        await Until(() => _log.ToString().Contains($"remote session s1: {Box} answered 500"));
    }
}
