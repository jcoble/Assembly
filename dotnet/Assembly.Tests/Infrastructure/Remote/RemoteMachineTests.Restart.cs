using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task A_remote_helper_that_finished_while_the_desktop_was_stopped_shows_idle_after_a_restart()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        await desktop.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");
        await Until(async () => await HelperStateAsync(parent, helperId) == "running");

        // The desktop stops as if before it copied any of the helper's rows, and the helper finishes on the box meanwhile.
        await _desktop!.DisposeAsync();
        ForgetRows(Path.Combine(_desktopHome, "assembly.db"), helperId);
        await box.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await StartDesktopAsync();

        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
    }

    [Fact]
    public async Task A_remote_helper_whose_turn_end_was_copied_but_not_applied_shows_idle_after_a_restart()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        // The desktop stops as if between copying the helper's turn end and showing its row idle.
        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await StartDesktopAsync();

        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
    }

    [Fact]
    public async Task A_remote_helper_sent_a_message_just_before_a_restart_stays_running_until_its_new_turn_ends()
    {
        var (parent, helperId, box) = await RemoteHelperIdleAsync();
        var before = (await RowsAsync(_desktop!, DesktopToken, parent)).Max(row => row.GetProperty("seq").GetInt64());

        // The desktop stops as if just after send_message showed the helper running, before it copied the box's new turn.
        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        using (var http = Client(_box, BoxToken))
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/sessions/{helperId}/send", Json("""{"text":"Which file?"}"""))).StatusCode);
        await box.CloseAsync();
        await box.OpenAsync();
        var secondPrompt = await box.ReadAsync("session/prompt");
        await StartDesktopAsync();

        await Until(async () => (await RowsAsync(_desktop!, DesktopToken, helperId)).LastOrDefault(row => row.GetProperty("kind").GetString() == "turn")
            is { } turn && turn.GetProperty("payload").GetProperty("state").GetString() == "started");
        Assert.Equal("running", await HelperStateAsync(parent, helperId));

        await box.ReplyAsync(secondPrompt, """{"stopReason":"end_turn"}""");
        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
        Assert.Single(await RowsAsync(_desktop!, DesktopToken, parent), row => row.GetProperty("seq").GetInt64() > before
            && row.GetProperty("itemId").GetString() == $"subagent:{helperId}" && row.GetProperty("payload").GetProperty("state").GetString() == "idle");
    }

    [Fact]
    public async Task A_remote_helper_shown_running_stays_running_after_a_restart_while_the_box_is_down()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await using var down = await StubAsync(_ => { }, () => 500);
        _desktop = await DesktopOnAsync(down, _desktopHome);

        // The follower gives up; only Resume or the stop button ends the row.
        await Until(() => _log.ToString().Contains($"remote session {helperId}: {Box} answered 500"));
        Assert.Equal("running", await HelperStateAsync(parent, helperId));
    }

    [Fact]
    public async Task A_remote_helper_shown_running_shows_idle_after_a_restart_whose_first_link_drops()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        // The first /live fails as a dropped link would; the next one opens and has nothing newer.
        var links = 0;
        await using var flaky = await StubAsync(app =>
        {
            app.UseWebSockets();
            app.Map("/live", async (HttpContext context) =>
            {
                if (Interlocked.Increment(ref links) == 1)
                {
                    context.Response.StatusCode = 500;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
            });
        });
        _desktop = await DesktopOnAsync(flaky, _desktopHome);

        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
        Assert.True(links >= 2);
    }

    [Fact]
    public async Task A_follower_whose_link_closes_at_once_reconnects_with_backoff()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        // Every /live opens and closes at once, so the follower never settles.
        var links = 0;
        await using var closing = await StubAsync(app =>
        {
            app.UseWebSockets();
            app.Map("/live", async (HttpContext context) =>
            {
                Interlocked.Increment(ref links);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
            });
        });
        _desktop = await DesktopOnAsync(closing, _desktopHome);

        // Backoff of 1 s, then 2 s: connects at about 0, 1 and 3 s.
        await Until(() => Volatile.Read(ref links) >= 1);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.InRange(Volatile.Read(ref links), 1, 3);
        Assert.Equal("running", await HelperStateAsync(parent, helperId));
    }

    [Fact]
    public async Task A_follower_whose_link_copies_a_row_and_then_closes_reconnects_after_a_second_each_time()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        // Every /live sends the next row and then closes: a healthy link, so the backoff stays at 1 s.
        var links = 0;
        await using var progressing = await StubAsync(app =>
        {
            app.UseWebSockets();
            app.Map("/live", async (HttpContext context) =>
            {
                Interlocked.Increment(ref links);
                var after = long.Parse(context.Request.Query["after"]!, CultureInfo.InvariantCulture);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var row = $$$"""{"type":"row","row":{"sessionId":"{{{helperId}}}","seq":{{{after + 1}}},"runId":null,"itemId":null,"kind":"text","payload":{"text":"x"},"createdAt":1,"prevSeq":{{{after}}}}}""";
                await socket.SendAsync(Encoding.UTF8.GetBytes(row), WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
            });
        });
        _desktop = await DesktopOnAsync(progressing, _desktopHome);

        // Connects at about 0, 1, 2 ... 6 s; doubling would give 0, 1 and 3.
        await Until(() => Volatile.Read(ref links) >= 1);
        await Task.Delay(TimeSpan.FromSeconds(6.5));
        Assert.True(Volatile.Read(ref links) >= 5, $"{links} connects");
    }

    [Fact]
    public async Task A_remote_helper_shown_running_shows_idle_after_a_restart_while_its_session_is_open()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await StartDesktopAsync();
        // Someone opens the helper's session before its follower settles; it stays open.
        using var watching = await OpenLiveAsync(_desktop!, DesktopToken, helperId, 0);

        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
        Assert.Equal(WebSocketState.Open, watching.State);
    }

    // A remote helper started by a parent here, whose first turn has ended and whose row here shows idle.
    private async Task<(string Parent, string HelperId, FakeLink Box)> RemoteHelperIdleAsync()
    {
        using var http = Client(_desktop!, DesktopToken);
        var created = await http.PostAsync("/sessions", Json("""{"agent":"claude","repoUrl":"https://github.com/o/r"}"""));
        var parent = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString()!;
        await SendAsync(parent);
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        await desktop.ReadAsync("session/prompt");
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", agent = "codex", machine = Box }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");
        await Until(async () => await HelperStateAsync(parent, helperId) == "running");
        await box.ReplyAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
        return (parent, helperId, box);
    }

    private async Task<string?> HelperStateAsync(string parent, string helperId) =>
        (await RowsAsync(_desktop!, DesktopToken, parent)).LastOrDefault(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}") is { } row
            ? row.GetProperty("payload").GetProperty("state").GetString()
            : null;

    private static void ForgetRows(string database, string sessionId)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM rows WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    private static void ShowRunning(string database, string parent, string helperId)
    {
        using var db = new SqliteConnection($"Data Source={database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE rows SET payload = json_set(payload, '$.state', 'running') WHERE session_id = $parent AND seq =
              (SELECT MAX(seq) FROM rows WHERE session_id = $parent AND item_id = $item AND kind = 'subagent');
            """;
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$item", $"subagent:{helperId}");
        command.ExecuteNonQuery();
    }
}
