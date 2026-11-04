using System.Net;
using Assembly.Tests.Draft.Base;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Remote;
using Assembly.Desktop.Serve;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Features.Chat.Sessions;

using Assembly.Tests.Features.Diagnostics;
namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>A desktop backend with a remote machine: a second backend on the same engine stands in for Workbox Test.</summary>
public sealed partial class RemoteMachineTests : IAsyncLifetime
{
    private const string DesktopToken = "desktop-token";
    private const string BoxToken = "box-token";
    private const string Box = "workbox-test";

    private readonly string _desktopHome = Directory.CreateTempSubdirectory("assembly-desktop-").FullName;
    private readonly string _boxHome = Directory.CreateTempSubdirectory("assembly-box-").FullName;
    private readonly FakeLauncher _desktopLauncher = new();
    private readonly FakeLauncher _boxLauncher = new();
    private readonly SharedLog _log = new();
    private DesktopBackend _box = null!;
    private Assembly.Infrastructure.Engine.EngineRuntime _boxEngine = null!;
    private DesktopBackend? _desktop;

    public async Task InitializeAsync()
    {
        _box = await DesktopBackend.StartAsync(_boxHome, BoxToken, port: 0, _boxLauncher, _log, engineStarted: runtime => _boxEngine = runtime);
        await StartDesktopAsync();
    }

    private async Task StartDesktopAsync()
    {
        _desktop = await DesktopBackend.StartAsync(_desktopHome, DesktopToken, port: 0, _desktopLauncher, _log, new RemoteConfig(Box, _box.Address, BoxToken));
        await _desktop.Connections.ReadyAsync(Box);
        var mirror = Assert.Single(_desktop.Connections.CaptureMirrorOwners()).Mirror;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => ready.TrySetResult();
        mirror.CaughtUp += CaughtUp;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is null)
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { mirror.CaughtUp -= CaughtUp; }
    }

    [Fact]
    public async Task A_session_made_on_the_remote_runs_its_turn_there_and_its_rows_are_copied_here()
    {
        Assert.Equal([Box], await MachineNamesAsync(_desktop!, DesktopToken));
        var sessionId = await CreateSessionAsync();
        Assert.Equal(Box, (await SessionAsync(_desktop!, DesktopToken, sessionId)).GetProperty("machine").GetString());
        Assert.Equal(JsonValueKind.Null, (await SessionAsync(_box, BoxToken, sessionId)).GetProperty("machine").ValueKind);

        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var clientId = await SendAsync(sessionId);
        await RunTurnOnBoxAsync();

        var streamed = await ApiTests.ReadUntilTurnEndsAsync(desktop);
        Assert.Equal(ApiTests.RunOfPrompt(streamed, sessionId, clientId), streamed[^1].GetProperty("runId").GetString());

        // Stored the same on both, the finished message's deltas deleted on each.
        using var box = await OpenLiveAsync(_box, BoxToken, sessionId, after: 0);
        using var reopened = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        Assert.Equal(Shape(await ApiTests.ReadUntilTurnEndsAsync(box)), Shape(await ApiTests.ReadUntilTurnEndsAsync(reopened)));
        Assert.Equal(0, _desktopLauncher.Launches);
    }

    [Fact]
    public async Task Opening_a_session_catches_up_on_a_turn_that_ran_while_nobody_watched()
    {
        var sessionId = await CreateSessionAsync();
        using (var owner = await ApiTests.OpenOwnerSocketAsync(_box.Address, BoxToken))
            await ApiTests.QueueSocketTurnAsync(owner, sessionId);
        await RunTurnOnBoxAsync();
        using var box = await OpenLiveAsync(_box, BoxToken, sessionId, after: 0);
        var ran = Shape(await ApiTests.ReadUntilTurnEndsAsync(box));

        await RowsPageAsync(_desktop!, DesktopToken, sessionId);
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);

        Assert.Equal(ran, Shape(await ApiTests.ReadUntilTurnEndsAsync(desktop)));
    }

    // App Nap is held for a remote's turn too, from its copied rows, and by a Mac restarted while it still runs.
    [Fact]
    public async Task A_remote_turn_holds_app_nap_here_across_a_mac_restart_until_it_ends()
    {
        var sessionId = await CreateSessionAsync();
        await SendAsync(sessionId);
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "app-nap-prompt");
        using (var watching = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0))
            await ApiTests.ReadRowsAsync(watching, 2);
        // The hub sends the rows before it applies their summary, which is what holds it.
        await Until(() => _desktop!.ActivityHeld);

        await _desktop!.DisposeAsync();
        await StartDesktopAsync();
        Assert.True(_desktop.ActivityHeld);

        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await agent.CloseAsync();
        using var desktop = await OpenLiveAsync(_desktop, DesktopToken, sessionId, after: 0);
        await ApiTests.ReadUntilTurnEndsAsync(desktop);
        await Until(() => !_desktop.ActivityHeld);
    }

    [Fact]
    public async Task A_mac_restarted_mid_turn_catches_up_instead_of_marking_the_turn_lost()
    {
        var sessionId = await CreateSessionAsync();
        using (var watching = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0))
        {
            await SendAsync(sessionId);
            var agent = await _boxLauncher.NextAsync();
            await agent.StartAsync();
            var prompt = await agent.ReadAsync("session/prompt");
            await agent.InsertAsync(prompt, "restart-prompt");
            await ApiTests.ReadRowsAsync(watching, 2);
            await _desktop!.DisposeAsync();

            await agent.TextAsync("Hello");
            await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
            await agent.CloseAsync();
        }

        await StartDesktopAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        var rows = await ApiTests.ReadUntilTurnEndsAsync(desktop);

        Assert.DoesNotContain(rows, row => row.GetProperty("kind").GetString() == "error");
        Assert.NotEqual("failed", rows[^1].GetProperty("payload").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_remote_that_writes_another_row_format_is_refused()
    {
        await using var other = new MachinesSocketTests.StubRemote { Format = 99 };
        await other.StartAsync();
        var address = other.Address;
        await using var desktop = await DesktopBackend.StartAsync(Directory.CreateTempSubdirectory("assembly-desktop-").FullName, DesktopToken, 0, _desktopLauncher, _log,
            new RemoteConfig(Box, address, BoxToken));

        using var owner = await ApiTests.OpenOwnerSocketAsync(desktop.Address, DesktopToken);
        var created = await ApiTests.SocketRequestAsync(owner, 1, "sessions", "create",
            JsonSerializer.Serialize(new { agent = "claude", clientId = Guid.NewGuid().ToString("N"), machine = Box }));

        Assert.Equal("error", created.GetProperty("t").GetString());
        Assert.Equal("unreachable", created.GetProperty("code").GetString());
        Assert.Contains("format 99", created.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_helper_of_another_agent_on_the_remote_keeps_its_agent_here()
    {
        var sessionId = await CreateSessionAsync();
        using var desktop = await OpenLiveAsync(_desktop!, DesktopToken, sessionId, after: 0);
        await SendAsync(sessionId);
        var agent = await _boxLauncher.NextAsync();
        var open = await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.InsertAsync(prompt, "agent-helper-prompt");
        var token = open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!;

        var helperId = (await ToolAsync(_box, token["Bearer ".Length..], "start_session",
            new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex" }))
            .GetProperty("sessionId").GetString()!;

        while ((await ApiTests.ReadRowsAsync(desktop, 1))[0].GetProperty("kind").GetString() != "subagent")
        {
        }
        var helper = await SessionAsync(_desktop!, DesktopToken, helperId);
        Assert.Equal(("codex", sessionId), (helper.GetProperty("agent").GetString(), helper.GetProperty("parentSessionId").GetString()));
    }

    [Fact]
    public async Task A_remote_helper_s_turn_end_doesnt_put_a_row_into_its_remote_parent_here()
    {
        var (parent, parentAgent, parentPrompt, helperId, helperAgent, helperPrompt) = await StartRemoteHelperOfRemoteParentAsync();

        // Follow the helper here, and let its turn end.
        await RowsPageAsync(_desktop!, DesktopToken, helperId);
        await Until(async () => (await RowsAsync(_desktop!, DesktopToken, helperId)).Any(row => row.GetProperty("kind").GetString() == "user"));
        await helperAgent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-helper");
        await Until(async () => (await RowsAsync(_desktop!, DesktopToken, parent)).Any(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}"
            && row.GetProperty("payload").GetProperty("state").GetString() == "idle"));

        // The parent's turn ends, and it runs another.
        await parentAgent.ReplyAsync(await parentAgent.ReadAsync("_session/steering"), """{"outcome":"injected"}""");
        await parentAgent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        await parentAgent.CloseAsync();
        await SendAsync(parent);
        await parentAgent.OpenAsync();
        await parentAgent.FinishAsync(await parentAgent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await Until(async () => (await RowsAsync(_box, BoxToken, parent)).Count(row => row.GetProperty("kind").GetString() == "turn"
            && row.GetProperty("payload").GetProperty("state").GetString() == "finished") == 2);
        var box = await RowsAsync(_box, BoxToken, parent);
        await Until(async () => (await RowsAsync(_desktop!, DesktopToken, parent)).Max(row => row.GetProperty("seq").GetInt64()) >= box.Max(row => row.GetProperty("seq").GetInt64()));
        var desktop = await RowsAsync(_desktop!, DesktopToken, parent);

        // Every row here is the box's copy, at the box's seq.
        static (long, string?, string?, long) Key(JsonElement row) =>
            (row.GetProperty("seq").GetInt64(), row.GetProperty("kind").GetString(), row.GetProperty("itemId").GetString(), row.GetProperty("createdAt").GetInt64());
        var copied = desktop.Select(Key).ToHashSet();
        foreach (var row in box.Where(row => row.GetProperty("kind").GetString() is "user" or "turn"))
            Assert.Contains(Key(row), copied);
        var boxRows = box.Select(Key).ToHashSet();
        foreach (var row in desktop)
            Assert.Contains(Key(row), boxRows);
    }

    // A session on the box whose agent starts a helper on the box, both mid-turn; the desktop knows the helper.
    private async Task<(string Parent, FakeLink ParentAgent, JsonElement ParentPrompt, string Helper, FakeLink HelperAgent, JsonElement HelperPrompt)>
        StartRemoteHelperOfRemoteParentAsync()
    {
        var parent = await CreateSessionAsync();
        await SendAsync(parent);
        var parentAgent = await _boxLauncher.NextAsync();
        var parentToken = SessionToken(await parentAgent.StartAsync());
        var parentPrompt = await parentAgent.ReadAsync("session/prompt");
        await parentAgent.InsertAsync(parentPrompt, "remote-parent-prompt");
        var helperId = (await ToolAsync(_box, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex" })).GetProperty("sessionId").GetString()!;
        var helperAgent = await _boxLauncher.NextAsync();
        await helperAgent.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await helperAgent.ReadAsync("session/prompt");
        await helperAgent.InsertAsync(helperPrompt, "remote-helper-prompt");
        await Until(async () => (await TopicStateAsync(_desktop!, DesktopToken, "sessions", helperId))
            .GetProperty("items").GetArrayLength() == 1);
        return (parent, parentAgent, parentPrompt, helperId, helperAgent, helperPrompt);
    }

    private static async Task<List<JsonElement>> RowsAsync(DesktopBackend backend, string token, string sessionId) =>
        (await RowsPageAsync(backend, token, sessionId, limit: 500)).GetProperty("rows").EnumerateArray().ToList();

    private static Task SocketSendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> SocketReadAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    [Fact]
    public async Task A_mac_session_starts_a_helper_on_the_remote_and_each_messages_the_other_once()
    {
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") });
        var parent = created.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        await desktop.InsertAsync(parentPrompt, "local-parent-prompt");

        var started = await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = await RemoteEngineMachineAsync() });
        var helperId = started.GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        var helperToken = SessionToken(await box.StartAsync(acpSessionId: "acp-helper"));
        var helperPrompt = await box.ReadAsync("session/prompt");
        await box.InsertAsync(helperPrompt, "cross-machine-helper-prompt");
        Assert.Contains("Count the lines.", helperPrompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(parent, (await SessionAsync(_box, BoxToken, helperId)).GetProperty("parentSessionId").GetString());
        var copy = await SessionAsync(_desktop!, DesktopToken, helperId);
        Assert.Equal((Box, "codex"), (copy.GetProperty("machine").GetString(), copy.GetProperty("agent").GetString()));

        // The parent's turn ends; its session stays open while its helper works on the box, once the desktop has that copy.
        await Until(async () => await HelperStateAsync(parent, helperId) == "running");
        await desktop.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-parent");

        // The helper asks its parent: queued on the box, picked up by the desktop, and delivered once.
        var asked = (await ToolAsync(_box, helperToken, "send_message", new { sessionId = parent, text = "Which file?" })).GetProperty("messageId").GetString()!;
        var question = await desktop.ReadAsync("session/prompt");
        Assert.Contains("Which file?", question.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await desktop.InsertAsync(question, "parent-question-provider");
        await Until(async () => (await ToolAsync(_box, helperToken, "message_status", new { messageId = asked }))
            .GetProperty("state").GetString() == "next_turn");
        Assert.Single((await RowsAsync(_desktop!, DesktopToken, parent)), row => row.GetProperty("kind").GetString() == "user"
            && row.GetProperty("payload").GetProperty("text").GetString()!.Contains("Which file?", StringComparison.Ordinal));
        await desktop.TextAsync("notes.txt", "p2", "acp-parent");
        await desktop.FinishAsync(question, """{"stopReason":"end_turn"}""");

        // The helper answers; the parent reads it on the desktop, from the rows copied from the box.
        await box.TextAsync("notes.txt has 3 lines.", "h1", "acp-helper");
        await box.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "acp-helper");
        await Until(async () => (await ToolAsync(_desktop!, parentToken, "message_status",
            new { messageId = started.GetProperty("messageId").GetString() })).GetProperty("state").GetString() == "next_turn");
        // The automatic final reply is its own durable message, not an invented answered state on the initial prompt.
        // The helper's turn end lets the kept parent session close, and the worker delivers the reply on its own, so
        // either can come first: the reply takes the session still open, or resumes it. Both deliver it once.
        var reply = await desktop.NextMessageAsync();
        if (reply.GetProperty("method").GetString() == "session/close")
        {
            await desktop.ReplyAsync(reply, "{}");
            await desktop.OpenAsync("acp-parent");
            reply = await desktop.ReadAsync("session/prompt");
        }
        Assert.Equal("session/prompt", reply.GetProperty("method").GetString());
        Assert.Contains("notes.txt has 3 lines.", reply.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Single((await RowsAsync(_desktop!, DesktopToken, parent)), row => row.GetProperty("kind").GetString() == "user"
            && row.GetProperty("payload").GetProperty("text").GetString()!.Contains("notes.txt has 3 lines.", StringComparison.Ordinal));
        await desktop.InsertAsync(reply, "helper-reply-provider");
        await desktop.FinishAsync(reply, """{"stopReason":"end_turn"}""");
        // Nothing is left on the session, so the next thing it hears is its close, not the reply again.
        await desktop.CloseAsync();
    }

    [Fact]
    public async Task A_message_left_queued_after_its_helper_ended_is_delivered_once_with_its_original_timestamp()
    {
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") });
        var parent = created.GetProperty("sessionId").GetString()!;
        var clientId = await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        await desktop.InsertAsync(parentPrompt, "local-parent-prompt");
        await ProviderInsertionAsync(_desktop!, DesktopToken, parent, clientId, "local-parent-prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = await RemoteEngineMachineAsync() }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        var helperToken = SessionToken(await box.StartAsync(acpSessionId: "acp-helper"));
        var helperPrompt = await box.ReadAsync("session/prompt");
        await box.InsertAsync(helperPrompt, "late-helper-provider");
        await _desktop!.DisposeAsync();
        _desktop = null;
        var late = (await ToolAsync(_box, helperToken, "send_message", new { sessionId = parent, text = "Which file?" }))
            .GetProperty("messageId").GetString()!;
        var sent = _boxEngine.Reader.Entity("message", late, default)!.Value.GetProperty("createdMs").GetInt64();
        await box.FinishAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await box.CloseAsync();
        await StartDesktopAsync();
        var resumed = await _desktopLauncher.NextAsync();
        await resumed.StartAsync("acp-parent");
        var prompt = await resumed.ReadAsync("session/prompt");
        Assert.Contains("Which file?", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(sent, ((Assembly.Engine.Hosting.IEngineRuntime)_boxEngine).Read(scope => scope.QueryFirst(
            "SELECT created_ms FROM engine_message WHERE id=$id", row => row.Long(0), new Assembly.Application.Common.Database.SqlArg("$id", late)), default));
        await resumed.InsertAsync(prompt, "late-provider");
        await resumed.FinishAsync(prompt, """{"stopReason":"end_turn"}""");

    }

    [Fact]
    public async Task Stop_on_a_remote_helper_s_row_stops_its_run_there_and_the_row_ends_stopped()
    {
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") });
        var parent = created.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        await desktop.InsertAsync(parentPrompt, "stop-parent-prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = await RemoteEngineMachineAsync() }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");
        await box.InsertAsync(helperPrompt, "stopped-helper-prompt");

        // The row's Stop: refused until the helper's run has been copied here, then it reaches the box.
        using var owner = await ApiTests.OpenOwnerSocketAsync(_desktop!.Address, DesktopToken);
        long stopId = 0;
        await Until(async () =>
        {
            var turns = await RowsAsync(_desktop!, DesktopToken, helperId);
            var runId = turns.LastOrDefault(row => row.GetProperty("kind").GetString() == "turn");
            if (runId.ValueKind == JsonValueKind.Undefined) return false;
            var stopped = await ApiTests.SocketRequestAsync(owner, ++stopId, "send", "stop",
                JsonSerializer.Serialize(new { runId = runId.GetProperty("runId").GetString() }));
            if (stopped.GetProperty("t").GetString() == "error")
            {
                Assert.Equal("failed", stopped.GetProperty("code").GetString());
                return false;
            }
            Assert.True(ApiTests.Result(stopped).GetProperty("ok").GetBoolean());
            return true;
        });
        var cancel = await box.ReadAsync("session/cancel");
        Assert.Equal("acp-helper", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        await box.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""", "acp-helper");

        await Until(async () =>
        {
            var rows = (await RowsPageAsync(_desktop!, DesktopToken, parent)).GetProperty("rows").EnumerateArray()
                .Where(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}").ToList();
            return rows.Count > 0 && rows[^1].GetProperty("payload") is var row
                && row.GetProperty("state").GetString() == "idle" && row.TryGetProperty("stopReason", out var reason) && reason.GetString() == "cancelled";
        });
    }

    [Fact]
    public async Task Ssh_exiting_marks_the_remote_unreachable_and_the_next_answer_marks_it_reachable()
    {
        // The tunnel's command exits each time this file appears.
        var exit = Path.Combine(Directory.CreateTempSubdirectory("assembly-tunnel-").FullName, "exit");
        await using var tunnel = new RemoteTunnel("/bin/sh", ["-c", $"while [ ! -e '{exit}' ]; do sleep 0.1; done; rm '{exit}'"], TextWriter.Null);
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

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadTextAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private Task<DesktopBackend> DesktopOnAsync(MachinesSocketTests.StubRemote remote, string home) =>
        DesktopBackend.StartAsync(home, DesktopToken, 0, _desktopLauncher, _log, new RemoteConfig(Box, remote.Address, BoxToken));

    private static async Task<string> ToolErrorAsync(DesktopBackend backend, string sessionToken, string tool, object arguments)
    {
        var answer = await ToolResultAsync(backend, sessionToken, tool, arguments);
        Assert.True(answer.GetProperty("isError").GetBoolean(), answer.GetRawText());
        return answer.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static string? MessageError(string database, string to)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT error FROM helper_messages WHERE to_session = $to;";
        command.Parameters.AddWithValue("$to", to);
        return command.ExecuteScalar() as string;
    }

    private static string SessionToken(JsonElement open) =>
        open.GetProperty("params").GetProperty("mcpServers")[0].GetProperty("headers")[0].GetProperty("value").GetString()!["Bearer ".Length..];

    private async Task<string> RemoteEngineMachineAsync() =>
        (await FeatureResultAsync(_box, BoxToken, "engine", "machine", new { })).GetProperty("machine").GetString()!;

    private static async Task<JsonElement> ToolAsync(DesktopBackend backend, string sessionToken, string tool, object arguments)
    {
        var answer = await ToolResultAsync(backend, sessionToken, tool, arguments);
        Assert.False(answer.GetProperty("isError").GetBoolean(), answer.GetRawText());
        return JsonDocument.Parse(answer.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    }

    private static async Task<JsonElement> ToolResultAsync(DesktopBackend backend, string sessionToken, string tool, object arguments)
    {
        using var http = Client(backend, sessionToken);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        var call = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        using var response = await http.PostAsync("/mcp", Json(call));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("result", out var result), body);
        return result.Clone();
    }

    private static Task Until(Func<bool> condition, TimeSpan? limit = null) => Until(() => Task.FromResult(condition()), limit);

    private static async Task Until(Func<Task<bool>> condition, TimeSpan? limit = null)
    {
        using var deadline = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(50, deadline.Token);
    }

    private async Task RunTurnOnBoxAsync()
    {
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        await agent.TextAsync("Hello");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    private static List<(long, string?)> Shape(List<JsonElement> rows) =>
        rows.Select(row => (row.GetProperty("seq").GetInt64(), row.GetProperty("kind").GetString())).ToList();

    private async Task<string> CreateSessionAsync()
    {
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N"), machine = Box });
        return created.GetProperty("sessionId").GetString()!;
    }

    private static async Task<JsonElement> FeatureResultAsync(DesktopBackend backend, string token, string feature, string method, object args, bool bulk = false)
    {
        using var socket = await ApiTests.OpenOwnerSocketAsync(backend.Address, token, bulk ? "/socket/bulk" : "/socket");
        return ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, feature, method, JsonSerializer.Serialize(args)));
    }

    private static async Task<JsonElement> TopicStateAsync(DesktopBackend backend, string token, string topic, string? key = null)
    {
        using var socket = await ApiTests.OpenOwnerSocketAsync(backend.Address, token);
        await SocketSendAsync(socket, JsonSerializer.Serialize(new { t = "sub", id = 1, topic, key }));
        var state = await ApiTests.ReadFrameAsync(socket);
        Assert.Equal("state", state.GetProperty("t").GetString());
        return state;
    }

    private static async Task<string[]> MachineNamesAsync(DesktopBackend backend, string token) =>
        (await TopicStateAsync(backend, token, "machines")).GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("item").GetProperty("name").GetString()).Where(name => name is not null).Select(name => name!).ToArray();

    private static async Task<JsonElement> SessionAsync(DesktopBackend backend, string token, string session) =>
        Assert.Single((await TopicStateAsync(backend, token, "sessions", session)).GetProperty("items").EnumerateArray()).GetProperty("item").Clone();

    // sessions.create returns before the owner's checkout settles and its start is delivered, and config.warm refuses with
    // checkout_not_ready until then. Like the app, wait on the owner's pushed Engine row instead of racing it.
    private async Task WaitSessionReadyAsync(string sessionId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var changed = new SemaphoreSlim(0);
        using var subscription = _boxEngine.Publisher.Register(_ => changed.Release(), out _);
        while (true)
        {
            var row = _boxEngine.Reader.Entity("session", sessionId, deadline.Token);
            if (row is { } session && session.GetProperty("checkoutState").GetString() == "ready"
                && session.GetProperty("state").GetString() == "idle" && session.GetProperty("activityState").GetString() == "idle")
                return;
            try { await changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail($"The owner's session never became ready: {row}");
            }
        }
    }

    private static Task<JsonElement> RowsPageAsync(DesktopBackend backend, string token, string session, int limit = 50) =>
        FeatureResultAsync(backend, token, "rows", "page", new { session, limit }, bulk: true);

    // Returns the committed client identity; the real run is correlated from pushed user rows.
    private async Task<string> SendAsync(string sessionId)
    {
        using var owner = await ApiTests.OpenOwnerSocketAsync(_desktop!.Address, DesktopToken);
        return await ApiTests.QueueSocketTurnAsync(owner, sessionId);
    }

    private static HttpClient Client(DesktopBackend backend, string token)
    {
        var http = new HttpClient { BaseAddress = backend.Address };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static Task<ClientWebSocket> OpenLiveAsync(DesktopBackend backend, string token, string sessionId, long after) =>
        ApiTests.OpenRowsAsync(backend.Address, token, sessionId, after);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task DisposeAsync()
    {
        if (_desktop is not null)
            await _desktop.DisposeAsync();
        await _box.DisposeAsync();
        Directory.Delete(_desktopHome, recursive: true);
        Directory.Delete(_boxHome, recursive: true);
    }
}
