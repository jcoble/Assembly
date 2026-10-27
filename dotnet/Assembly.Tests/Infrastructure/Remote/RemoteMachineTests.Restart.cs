using System.Text.Json;
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

        // The desktop stops as if between copying the helper's turn end and showing its row idle.
        await _desktop!.DisposeAsync();
        ShowRunning(Path.Combine(_desktopHome, "assembly.db"), parent, helperId);
        await StartDesktopAsync();

        await Until(async () => await HelperStateAsync(parent, helperId) == "idle");
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
