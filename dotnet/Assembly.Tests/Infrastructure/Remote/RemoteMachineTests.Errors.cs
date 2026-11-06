using Assembly.Tests.Infrastructure.Http;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Agents;
using Assembly.Tests.Draft.Base;
using Assembly.Infrastructure.Remote;
using Assembly.Desktop.Serve;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_registered_socket_call_reports_the_remote_error_without_an_internal_failure()
    {
        await using var remote = new MachinesSocketTests.StubRemote
        {
            SocketBackend = _box.Address,
            SocketFailure = frame => frame.GetProperty("feature").GetString() == "machines" ? "boom" : null
        };
        await remote.StartAsync();
        await using var desktop = await DesktopOnAsync(remote, Directory.CreateTempSubdirectory("assembly-desktop-").FullName);
        await OwnerVerifiedAsync(desktop);

        using var socket = await ApiTests.OpenOwnerSocketAsync(desktop.Address, DesktopToken);
        var created = await ApiTests.SocketRequestAsync(socket, 1, "machines", "agents",
            JsonSerializer.Serialize(new { machine = Box }));

        Assert.Equal("error", created.GetProperty("t").GetString());
        Assert.Equal("failed", created.GetProperty("code").GetString());
        Assert.Contains("boom", created.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_helper_tools_report_remote_call_errors_and_the_outbox_logs_HTTP_errors()
    {
        var startFailures = 0;
        var outboxRequests = 0;
        await using var remote = new MachinesSocketTests.StubRemote
        {
            SocketBackend = _box.Address,
            SocketFailure = frame => frame.GetProperty("feature").GetString() == "engine"
                && frame.GetProperty("method").GetString() == "foreign_title_begin"
                && Interlocked.Increment(ref startFailures) <= 2 ? "boom" : null
        };
        await remote.StartAsync(app => app.MapGet("/outbox", () =>
        {
            Interlocked.Increment(ref outboxRequests);
            return Results.Text("outbox down", statusCode: 500);
        }));
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        await using var desktop = await DesktopOnAsync(remote, home);
        await OwnerVerifiedAsync(desktop);
        var parent = (await RestartCallAsync(desktop, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") })).GetProperty("sessionId").GetString()!;
        await RestartSendAsync(desktop, DesktopToken, parent, "hi", "errors-parent");
        var agent = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await agent.StartAsync(acpSessionId: "acp-parent"));
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "errors-parent-provider");
        await ProviderInsertionAsync(desktop, DesktopToken, parent, "errors-parent", "errors-parent-provider");
        var engineMachine = (await RestartCallAsync(_box, BoxToken, "engine", "machine", new { })).GetProperty("machine").GetString()!;
        var start = new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = engineMachine };

        Assert.Contains("boom", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Equal(1, Volatile.Read(ref startFailures));
        Assert.Contains("boom", await ToolErrorAsync(desktop, parentToken, "start_session", start));
        Assert.Equal(2, Volatile.Read(ref startFailures));

        // The real remote owns the accepted child, failed provider operation and message outcomes.
        _boxLauncher.Launch = _ => Task.FromException<IAgentLink>(new CallFailed("boom"));
        var child = await ToolAsync(desktop, parentToken, "start_session", start);
        var helperId = child.GetProperty("sessionId").GetString()!;
        await FailedMessageAsync(desktop, parentToken, child.GetProperty("messageId").GetString()!);
        var sent = await ToolAsync(desktop, parentToken, "send_message", new { sessionId = helperId, text = "Which file?" });
        Assert.Equal("queued", sent.GetProperty("state").GetString());
        await FailedMessageAsync(desktop, parentToken, sent.GetProperty("messageId").GetString()!);
        Assert.Equal(0, Volatile.Read(ref outboxRequests));
    }

    private async Task FailedMessageAsync(Assembly.Desktop.Serve.DesktopBackend desktop, string token, string messageId)
    {
        JsonElement status = default;
        await Until(async () =>
        {
            status = await ToolAsync(desktop, token, "message_status", new { messageId });
            return status.GetProperty("state").GetString() == "failed";
        });
        Assert.Equal(messageId, status.GetProperty("messageId").GetString());
        Assert.Contains("boom", status.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_remote_that_drops_its_welcome_does_not_fault_the_follower()
    {
        await using var remote = new MachinesSocketTests.StubRemote { SocketBackend = _box.Address };
        await remote.StartAsync();
        var home = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
        string sessionId;
        await using (var first = await DesktopOnAsync(remote, home))
        {
            await OwnerVerifiedAsync(first);
            var created = await RestartCallAsync(first, DesktopToken, "sessions", "create",
                new { agent = "claude", clientId = Guid.NewGuid().ToString("N"), machine = Box });
            sessionId = created.GetProperty("sessionId").GetString()!;
            Assert.Equal("claude", (await SessionAsync(_box, BoxToken, sessionId)).GetProperty("agent").GetString());
        }

        // A new desktop checks the format again when opening the same owning session.
        remote.DropVersion = true;
        await using var desktop = await DesktopOnAsync(remote, home);
        await RowsPageAsync(desktop, DesktopToken, sessionId);

        await Until(() => _log.ToString().Contains($"remote session {sessionId}: {Box} didn't answer"));
    }

    [Fact]
    public async Task Ssh_exiting_marks_the_remote_unreachable_and_the_next_answer_marks_it_reachable()
    {
        // The tunnel's command exits each time this file appears.
        var exit = Path.Combine(Directory.CreateTempSubdirectory("assembly-tunnel-").FullName, "exit");
        await using var tunnel = RemoteTunnel.Wrapped(["/bin/sh", "-c", $"while [ ! -e '{exit}' ]; do sleep 0.1; done; rm '{exit}'"], TextWriter.Null);
        await using var desktop = await DesktopBackend.StartAsync(Directory.CreateTempSubdirectory("assembly-desktop-").FullName, DesktopToken, 0,
            _desktopLauncher, _log, new RemoteConfig(Box, _box.Address, BoxToken), tunnel);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{desktop.Address.Authority}/socket"), CancellationToken.None);
        await SendTextAsync(socket, $$"""{"t":"hello","token":"{{DesktopToken}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadTextAsync(socket));
        await SendTextAsync(socket, """{"t":"sub","id":1,"topic":"machines"}""");
        Assert.Contains("\"reachable\":true", await ReadTextAsync(socket));

        File.WriteAllText(exit, "");
        Assert.Contains("\"reachable\":false,\"reason\":\"ssh tunnel exited\"", await ReadTextAsync(socket));

        await SendTextAsync(socket, $$$"""{"t":"call","id":2,"feature":"machines","method":"agents","args":{"machine":"{{{Box}}}"}}""");
        Assert.Contains("\"reachable\":true", await ReadTextAsync(socket));
        Assert.Contains("\"result\":", await ReadTextAsync(socket));
    }
}
