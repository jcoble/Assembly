using System.Globalization;
using Assembly.Desktop.Serve;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Draft.Base;
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
        var parent = (await RestartCallAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") })).GetProperty("sessionId").GetString()!;
        await RestartSendAsync(_desktop!, DesktopToken, parent, "hi", "restart-parent");
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        await desktop.InsertAsync(parentPrompt, "restart-parent-provider");
        await ProviderInsertionAsync(_desktop!, DesktopToken, parent, "restart-parent", "restart-parent-provider");
        var engineMachine = (await RestartCallAsync(_box, BoxToken, "engine", "machine", new { })).GetProperty("machine").GetString()!;
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = engineMachine }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");
        await UntilRestartStateAsync(parent, helperId, "running");

        // The desktop stops as if before it copied any of the helper's rows, and the helper finishes on the box meanwhile.
        await _desktop!.DisposeAsync();
        ForgetRows(Path.Combine(_desktopHome, "assembly.db"), helperId);
        await box.FinishAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await StartDesktopAsync();

        await UntilRestartStateAsync(parent, helperId, "idle");
    }

    [Fact]
    public async Task A_remote_helper_whose_turn_end_was_copied_but_not_applied_shows_idle_after_a_restart()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        // The desktop stops as if between copying the helper's turn end and showing its row idle.
        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await StartDesktopAsync();

        await UntilRestartStateAsync(parent, helperId, "idle");
    }

    [Fact]
    public async Task A_remote_helper_sent_a_message_just_before_a_restart_stays_running_until_its_new_turn_ends()
    {
        var (parent, helperId, box) = await RemoteHelperIdleAsync();
        var before = (await RowsAsync(_desktop!, DesktopToken, parent)).Max(row => row.GetProperty("seq").GetInt64());

        // The desktop stops as if just after send_message showed the helper running, before it copied the box's new turn.
        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await RestartSendAsync(_box, BoxToken, helperId, "Which file?", "restart-helper");
        await box.CloseAsync();
        await box.OpenAsync();
        var secondPrompt = await box.ReadAsync("session/prompt");
        await box.InsertAsync(secondPrompt, "restart-helper-provider");
        await ProviderInsertionAsync(_box, BoxToken, helperId, "restart-helper", "restart-helper-provider");
        await StartDesktopAsync();

        await Until(async () => (await RestartHelperRowsAsync(helperId)).LastOrDefault(row => row.GetProperty("kind").GetString() == "turn")
            is { } turn && turn.GetProperty("payload").GetProperty("state").GetString() == "started");
        Assert.Equal("running", await HelperStateAsync(parent, helperId));

        await box.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""",
            secondPrompt.GetProperty("params").GetProperty("sessionId").GetString()!);
        await UntilRestartStateAsync(parent, helperId, "idle");
        Assert.Single(await RowsAsync(_desktop!, DesktopToken, parent), row => row.GetProperty("seq").GetInt64() > before
            && row.GetProperty("itemId").GetString() == $"subagent:{helperId}" && row.GetProperty("payload").GetProperty("state").GetString() == "idle");
    }

    [Fact]
    public async Task A_remote_helper_shown_running_stays_running_after_a_restart_while_the_box_is_down()
    {
        var (parent, helperId, box) = await RemoteHelperIdleAsync();

        // Establish real new work and its last-known running mirror before the owner becomes unreachable.
        await RestartSendAsync(_box, BoxToken, helperId, "Which file?", "restart-offline-helper");
        await box.CloseAsync();
        await box.OpenAsync();
        var prompt = await box.ReadAsync("session/prompt");
        await box.InsertAsync(prompt, "restart-offline-helper-provider");
        await ProviderInsertionAsync(_box, BoxToken, helperId, "restart-offline-helper", "restart-offline-helper-provider");
        await UntilRestartStateAsync(parent, helperId, "running");

        await _desktop!.DisposeAsync();
        await using var down = new MachinesSocketTests.StubRemote { DropVersion = true };
        await down.StartAsync();
        _desktop = await DesktopOnAsync(down, _desktopHome);

        // The actual probe fails; a log from the former row follower is not its completion contract.
        await RestartBoxUnavailableAsync(down);
        Assert.Equal("running", await HelperStateAsync(parent, helperId));
    }

    [Fact]
    public async Task A_remote_helper_shown_running_shows_idle_after_a_restart_whose_first_link_drops()
    {
        var (parent, helperId, _) = await RemoteHelperIdleAsync();

        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        // The first machine socket drops. Its next subscription catches up on the same helper.
        await using var flaky = new MachinesSocketTests.StubRemote { SocketBackend = _box.Address, DropWelcomes = 1 };
        await flaky.StartAsync();
        _desktop = await DesktopOnAsync(flaky, _desktopHome);

        await UntilRestartStateAsync(parent, helperId, "idle");
        Assert.True(flaky.VersionHits >= 2);
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

        await UntilRestartStateAsync(parent, helperId, "idle");
        Assert.Equal(WebSocketState.Open, watching.State);
    }

    // A remote helper started by a parent here, whose first turn has ended and whose row here shows idle.
    private async Task<(string Parent, string HelperId, FakeLink Box)> RemoteHelperIdleAsync()
    {
        var parent = (await RestartCallAsync(_desktop!, DesktopToken, "sessions", "create",
            new { agent = "claude", clientId = Guid.NewGuid().ToString("N") })).GetProperty("sessionId").GetString()!;
        await RestartSendAsync(_desktop!, DesktopToken, parent, "hi", "restart-parent");
        var desktop = await _desktopLauncher.NextAsync();
        var parentToken = SessionToken(await desktop.StartAsync(acpSessionId: "acp-parent"));
        var parentPrompt = await desktop.ReadAsync("session/prompt");
        await desktop.InsertAsync(parentPrompt, "restart-parent-provider");
        await ProviderInsertionAsync(_desktop!, DesktopToken, parent, "restart-parent", "restart-parent-provider");
        var engineMachine = (await RestartCallAsync(_box, BoxToken, "engine", "machine", new { })).GetProperty("machine").GetString()!;
        var helperId = (await ToolAsync(_desktop!, parentToken, "start_session", new { prompt = "Count the lines.", title = "Count the lines.", provider = "codex", folder = _boxHome, machine = engineMachine }))
            .GetProperty("sessionId").GetString()!;
        var box = await _boxLauncher.NextAsync();
        await box.StartAsync(acpSessionId: "acp-helper");
        var helperPrompt = await box.ReadAsync("session/prompt");
        await UntilRestartStateAsync(parent, helperId, "running");
        await box.FinishAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        await UntilRestartStateAsync(parent, helperId, "idle");
        return (parent, helperId, box);
    }

    private static async Task RestartSendAsync(DesktopBackend backend, string token, string session, string text, string clientId)
    {
        var accepted = await RestartCallAsync(backend, token, "send", "send", new { session, text, clientId });
        Assert.Equal(session, accepted.GetProperty("sessionId").GetString());
        Assert.Equal($"prompt/{session}/{clientId}", accepted.GetProperty("messageId").GetString());
        Assert.Equal(clientId, accepted.GetProperty("clientId").GetString());
        Assert.Equal("queued", accepted.GetProperty("state").GetString());
        Assert.True(accepted.GetProperty("messageVersion").GetInt64() > 0);
        Assert.True(accepted.GetProperty("seq").GetInt64() > 0);
        Assert.True(accepted.GetProperty("epoch").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("runId").ValueKind);
    }

    private static async Task ProviderInsertionAsync(DesktopBackend backend, string token, string session, string clientId, string providerMessageId)
    {
        using var socket = await ApiTests.OpenOwnerSocketAsync(backend.Address, token);
        var messageId = $"prompt/{session}/{clientId}";
        await SocketSendAsync(socket, JsonSerializer.Serialize(new { t = "sub", id = 1, stream = "engine", topic = "messages", key = messageId }));
        while (true)
        {
            var frame = await ApiTests.ReadFrameAsync(socket);
            var kind = frame.GetProperty("t").GetString();
            Assert.True(kind is "engine_snapshot" or "engine_events", frame.GetRawText());
            var inserted = kind == "engine_snapshot"
                ? frame.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == messageId && item.GetProperty("state").GetString() == "next_turn")
                : frame.GetProperty("events").EnumerateArray().Any(item => item.GetProperty("entityId").GetString() == messageId && item.GetProperty("data").GetProperty("state").GetString() == "next_turn");
            var more = frame.GetProperty("more").GetBoolean();
            if (kind != "engine_snapshot" || !more)
                await SocketSendAsync(socket, JsonSerializer.Serialize(new { t = "ack", id = 1, stream = "engine", after = frame.GetProperty("through").GetInt64() }));
            if (inserted) break;
            if (more) await SocketSendAsync(socket, "{\"t\":\"next\",\"id\":1}");
        }
        var page = await RowsPageAsync(backend, token, session);
        var user = Assert.Single(page.GetProperty("rows").EnumerateArray(), row => row.GetProperty("kind").GetString() == "user" &&
            row.GetProperty("payload").TryGetProperty("clientId", out var client) && client.GetString() == clientId);
        Assert.False(string.IsNullOrEmpty(user.GetProperty("runId").GetString()));
        var acknowledgment = user.GetProperty("payload").GetProperty("acknowledgment");
        Assert.Equal("prompt_inserted", acknowledgment.GetProperty("kind").GetString());
        Assert.Equal(providerMessageId, acknowledgment.GetProperty("providerMessageId").GetString());
    }

    private static async Task<JsonElement> RestartCallAsync(DesktopBackend backend, string token, string feature, string method, object args)
    {
        using var socket = await ApiTests.OpenOwnerSocketAsync(backend.Address, token);
        return ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, feature, method, JsonSerializer.Serialize(args)));
    }

    private async Task<List<JsonElement>> RestartHelperRowsAsync(string helperId)
    {
        using var socket = await ApiTests.OpenOwnerSocketAsync(_desktop!.Address, DesktopToken, "/socket/bulk");
        var frame = await ApiTests.SocketRequestAsync(socket, 1, "rows", "page", JsonSerializer.Serialize(new { session = helperId, limit = 500 }));
        if (frame.GetProperty("t").GetString() == "error")
        {
            var code = frame.TryGetProperty("code", out var errorCode) ? errorCode.GetString() : null;
            var reason = frame.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : null;
            Console.Error.WriteLine("restart helper rows failed: code=" +
                (code is "failed" or "bad_args" or "unavailable" or "unreachable" or "forbidden" ? code : "other") +
                "; reason=" + (reason == "unknown session" ? "unknown_session" : "other"));
        }
        // Keep the same result assertion and rows/page request; only the safe diagnostic is new.
        return ApiTests.Result(frame).GetProperty("rows").EnumerateArray().ToList();
    }

    private async Task RestartBoxUnavailableAsync(MachinesSocketTests.StubRemote down)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await down.VersionStarted.Task.WaitAsync(deadline.Token);
        using var socket = await ApiTests.OpenOwnerSocketAsync(_desktop!.Address, DesktopToken).WaitAsync(deadline.Token);
        await SocketSendAsync(socket, "{\"t\":\"sub\",\"id\":1,\"topic\":\"machines\"}").WaitAsync(deadline.Token);
        while (true)
        {
            var frame = await ApiTests.ReadFrameAsync(socket).WaitAsync(deadline.Token);
            var type = frame.GetProperty("t").GetString();
            Assert.True(type is "state" or "change");
            IEnumerable<JsonElement> items = type == "state"
                ? frame.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("item"))
                : [frame.GetProperty("item")];
            if (items.Any(item => item.GetProperty("name").GetString() == Box &&
                item.GetProperty("checked").GetBoolean() && !item.GetProperty("reachable").GetBoolean())) return;
        }
    }

    private async Task<string?> HelperStateAsync(string parent, string helperId) =>
        (await RowsAsync(_desktop!, DesktopToken, parent)).LastOrDefault(row => row.GetProperty("itemId").GetString() == $"subagent:{helperId}") is { } row
            ? row.GetProperty("payload").GetProperty("state").GetString()
            : null;

    private async Task UntilRestartStateAsync(string parent, string helperId, string expected)
    {
        try { await Until(async () => await HelperStateAsync(parent, helperId) == expected); }
        catch (OperationCanceledException)
        {
            // Diagnostics cannot change the wait's deadline, assertion, or original exception.
            try
            {
                Console.Error.WriteLine($"restart helper diagnostic expected={expected}: " + RestartProducerErrors());
                RestartDatabaseStatus("owner", _boxHome, parent, helperId);
                RestartDatabaseStatus("desktop", _desktopHome, parent, helperId);
            }
            catch (Exception error)
            {
                // Never print arbitrary exception messages, paths, or captured provider output.
                try
                {
                    Console.Error.WriteLine("restart idle diagnostic unavailable: " +
                    (error is SqliteException sqlite
                        ? "sqlite code=" + sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)
                        : "diagnostic"));
                }
                catch { /* Even a failed diagnostic sink must preserve the original timeout. */ }
            }
            throw;
        }
    }

    private string RestartProducerErrors()
    {
        var terminalErrors = 0;
        var feedErrors = 0;
        var codes = new List<string>();
        using var lines = new StringReader(_log.ToString());
        while (lines.ReadLine() is { } line)
        {
            const string terminal = ": the turn's end wasn't saved: ";
            const string feed = "engine feed: ";
            var at = line.IndexOf(terminal, StringComparison.Ordinal);
            string? code = null;
            if (at >= 0) { terminalErrors++; code = line[(at + terminal.Length)..]; }
            else if (line.StartsWith(feed, StringComparison.Ordinal))
            { feedErrors++; code = line[feed.Length..]; }
            // Exact known producer codes only. Unstructured errors remain a category/count.
            if (code is "turn_reply_changed" or "turn_reply_refused" or "provider_fact_refused" or
                "invalid_transition" or "wrong_epoch" or "stale_epoch" or "unknown_session" or
                "forbidden" or "bad_args" or "fact_refused") codes.Add(code);
        }
        return $"terminal_write_errors={terminalErrors}; feed_errors={feedErrors}; codes={string.Join(',', codes.Distinct())}";
    }

    private static void RestartDatabaseStatus(string label, string home, string parent, string helperId)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(home, "assembly.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT 'session',s.state,s.activity_state,s.epoch,s.version,r.state
              FROM engine_session s LEFT JOIN engine_run r ON r.id=s.run_id
              WHERE s.id=$helper AND s.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
            UNION ALL
            SELECT 'mirror',json_extract(r.row_json,'$.state'),json_extract(r.row_json,'$.activityState'),
                json_extract(r.row_json,'$.epoch'),r.version,m.state
              FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
              WHERE r.entity_kind='session' AND r.entity_id=$helper AND m.name=$box
            LIMIT 3;
            SELECT seq,json_extract(payload,'$.state') FROM rows
              WHERE session_id=$helper AND kind='turn' ORDER BY seq DESC LIMIT 3;
            SELECT seq,json_extract(payload,'$.state') FROM rows
              WHERE session_id=$parent AND kind='subagent' AND item_id=$item ORDER BY seq DESC LIMIT 1;
            SELECT seq,kind,late FROM engine_event
              WHERE entity_kind='session' AND entity_id=$helper
                AND machine=(SELECT machine_id FROM engine_meta WHERE singleton=1)
              ORDER BY seq DESC LIMIT 3;
            SELECT EXISTS(SELECT 1 FROM engine_message m WHERE m.to_session_id=$helper
                AND m.state='queued' AND m.in_flight=1
                AND m.machine=(SELECT machine_id FROM engine_meta WHERE singleton=1));
            -- DisplayQueries does not publish in_flight; NULL is unknown, never proof of false.
            SELECT r.version,json_extract(r.row_json,'$.state'),NULL
              FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation
              WHERE r.entity_kind='message' AND json_extract(r.row_json,'$.toSessionId')=$helper
                AND m.name=$box AND m.state='live'
              ORDER BY r.version DESC,r.entity_id LIMIT 3;
            """;
        command.Parameters.AddWithValue("$helper", helperId);
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$item", "subagent:" + helperId);
        command.Parameters.AddWithValue("$box", Box);
        using var reader = command.ExecuteReader();
        var section = 0;
        do
        {
            var count = 0;
            while (reader.Read())
            {
                count++;
                var fields = new List<string>();
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    if (reader.IsDBNull(column)) { fields.Add("null"); continue; }
                    if (reader.GetFieldType(column) == typeof(long))
                    { fields.Add(reader.GetInt64(column).ToString(CultureInfo.InvariantCulture)); continue; }
                    var value = reader.GetString(column);
                    fields.Add(value is "session" or "mirror" or "starting" or "running" or "idle" or
                        "needs_me" or "lost" or "ended" or "foreign" or "handing_off" or "moved" or
                        "waiting" or "queued" or "next_turn" or "steer_injected" or "expired" or
                        "completed" or "failed" or "cancelled" or "finished" or "started" or
                        "snapshotting" or "live" or "retired" or "session.changed" ? value : "other");
                }
                Console.Error.WriteLine($"restart idle diagnostic {label} section={section}: {string.Join(',', fields)}");
            }
            if (count == 0) Console.Error.WriteLine($"restart idle diagnostic {label} section={section}: absent");
            section++;
        } while (reader.NextResult());
    }

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
