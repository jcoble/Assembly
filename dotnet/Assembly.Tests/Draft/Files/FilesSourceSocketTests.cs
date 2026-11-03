using System.Text.Json;

namespace Assembly.Tests.Draft.Files;

public sealed partial class FilesSocketTests
{
    [Fact]
    public async Task A_draft_cannot_begin_against_a_replacement_checkout_even_with_identical_file_bytes()
    {
        var first = Directory.CreateDirectory(Path.Combine(_checkouts.FullName, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_checkouts.FullName, "second")).FullName;
        await File.WriteAllTextAsync(Path.Combine(first, "a"), "same");
        await File.WriteAllTextAsync(Path.Combine(second, "a"), "same");
        var session = _sessions.Create("codex", first, workingFolder: first);
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(normal, 1, "owner", $$"""{"session":"{{session.SessionId}}"}""");
        var original = JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("result").GetRawText();
        await CallAsync(bulk, 2, "read", $$"""{"session":"{{session.SessionId}}","path":"a"}""");
        var revision = JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("revision").GetString();
        _db.Writer.Write("test.move-checkout", scope => scope.Execute(
            "UPDATE session_info SET working_folder=$root WHERE session_id=$id",
            new("$root", second), new("$id", session.SessionId)));
        await CallAsync(bulk, 3, "beginSave", $$"""{"session":"{{session.SessionId}}","path":"a","expectedRevision":"{{revision}}","size":3,"expectedOwner":{{original}}}""");
        var refused = await ReadAsync(bulk);
        Assert.Equal("error", JsonDocument.Parse(refused).RootElement.GetProperty("t").GetString());
        Assert.Contains("checkout changed", refused);
        Assert.Empty(Directory.GetFiles(first, ".assembly-save-*"));
        Assert.Empty(Directory.GetFiles(second, ".assembly-save-*"));
        Assert.Equal("same", await File.ReadAllTextAsync(Path.Combine(first, "a")));
        Assert.Equal("same", await File.ReadAllTextAsync(Path.Combine(second, "a")));
    }

    [Fact]
    public async Task Qualified_file_reads_refuse_wrong_source_before_disclosing_the_local_file()
    {
        var session = _sessions.Create("codex", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "private.txt"), "only the actual source");
        var machine = _db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), default) ?? throw new InvalidOperationException("missing actual engine source");
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        foreach (var source in new[]
        {
            new { machine = "old-owner", generation = 0, routeMachine = (string?)null },
            new { machine, generation = 1, routeMachine = (string?)null },
            new { machine, generation = 0, routeMachine = (string?)"old-route" }
        })
        {
            var expected = JsonSerializer.Serialize(source);
            await CallAsync(normal, 1, "owner", $$"""{"session":"{{session.SessionId}}","expectedSource":{{expected}}}""");
            Assert.Equal("error", JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("t").GetString());
            await CallAsync(bulk, 2, "read", $$"""{"session":"{{session.SessionId}}","path":"private.txt","expectedSource":{{expected}}}""");
            var refused = await ReadAsync(bulk);
            Assert.Equal("error", JsonDocument.Parse(refused).RootElement.GetProperty("t").GetString());
            Assert.DoesNotContain("only the actual source", refused);
        }
        await CallAsync(bulk, 3, "read", $$$"""{"session":"{{{session.SessionId}}}","path":"private.txt","expectedSource":{"machine":"{{{machine}}}","generation":0,"routeMachine":null}}""");
        Assert.Equal("only the actual source", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("content").GetString());
    }
}
