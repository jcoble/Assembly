using System.Text.Json;
namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitSocketTests
{
    // Pins: g9#1, g9#6
    [Fact]
    public async Task Writes_stage_the_literal_file_and_refuse_escape_without_changing_it()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r"); var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root); await File.WriteAllTextAsync(Path.Combine(root, "-a[1].txt"), "new\n");
        using var socket = await OpenAsync();
        await SendAsync(socket, JsonSerializer.Serialize(new { t = "call", id = 1, feature = "git", method = "stage", args = new { session = session.SessionId, paths = new[] { "-a[1].txt" } } }));
        using var result = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", result.RootElement.GetProperty("t").GetString());
        Assert.Contains(result.RootElement.GetProperty("result").GetProperty("status").GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "-a[1].txt" && file.GetProperty("index").GetString() == "A");
        await SendAsync(socket, JsonSerializer.Serialize(new { t = "call", id = 2, feature = "git", method = "stage", args = new { session = session.SessionId, paths = new[] { "../outside" } } }));
        await AssertErrorAsync(socket, "that path is outside the session's folder");
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(root, "-a[1].txt")));
    }

    /// <summary>The desktop sends a rename's current name; the backend restores the old one from the reviewed rename.</summary>
    [Fact]
    public async Task Discarding_a_staged_rename_restores_the_old_name()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r"); var root = _sessions.Item(session).Root!;
        await MakeRepoAsync(root); await GitAsync(root, "mv", "tracked.txt", "renamed.txt");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "git", "status", session.SessionId);
        using var status = JsonDocument.Parse(await ReadAsync(socket));
        var files = status.RootElement.GetProperty("result").GetProperty("files");
        Assert.Equal("tracked.txt", Assert.Single(files.EnumerateArray()).GetProperty("originalPath").GetString());
        await SendAsync(socket, JsonSerializer.Serialize(new { t = "call", id = 2, feature = "git", method = "discard", args = new { session = session.SessionId, tracked = new[] { "renamed.txt" }, untracked = Array.Empty<string>(), expected = files } }));
        using var result = JsonDocument.Parse(await ReadAsync(socket));
        Assert.Equal("result", result.RootElement.GetProperty("t").GetString());
        Assert.Equal("committed", await File.ReadAllTextAsync(Path.Combine(root, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(root, "renamed.txt")));
    }
}
