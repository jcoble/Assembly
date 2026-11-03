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
}
