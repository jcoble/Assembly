using System.Text.Json;
using Assembly.Infrastructure.Chat;

namespace Assembly.Tests.Draft.Files;

public sealed partial class FilesSocketTests
{
    /// <summary>A forward names this machine as the source of a viewed folder, which has no session row to check it against.</summary>
    [Fact]
    public async Task A_viewed_folder_forwarded_here_lists_with_this_machine_as_its_source()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_checkouts.FullName, "viewed")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "one");
        var machine = _db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), default);
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "list", $$$"""{"session":"{{{FolderView.Id("p1", folder)}}}","dir":"","expectedSource":{"machine":"{{{machine}}}","generation":0,"routeMachine":null}}""");
        var answer = JsonDocument.Parse(await ReadAsync(socket)).RootElement;
        Assert.Equal("a.txt", answer.GetProperty("result").GetProperty("entries")[0].GetProperty("path").GetString());
    }

    /// <summary>A session can start in a viewed folder between a save's beginning and its commit; its files win.</summary>
    [Fact]
    public async Task A_save_begun_on_a_viewed_folder_is_refused_when_a_session_starts_there_before_it_commits()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_checkouts.FullName, "viewed-save")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "a"), "old");
        var view = FolderView.Id("p1", folder);
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(bulk, 1, "read", $$"""{"session":"{{view}}","path":"a"}""");
        var revision = JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("revision").GetString();
        await CallAsync(bulk, 2, "beginSave", $$"""{"session":"{{view}}","path":"a","expectedRevision":"{{revision}}","size":3}""");
        var upload = JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("upload").GetString();
        await CallAsync(bulk, 3, "saveChunk", $$"""{"upload":"{{upload}}","offset":0,"data":"bmV3"}""");
        Assert.Equal("result", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("t").GetString());
        _db.Writer.Write("test.session-starts", scope => scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,folder,title,state,activity_state,holder,epoch,cancel_requested)
            SELECT $id,0,machine_id,0,0,'claude',$folder,'Busy session','running','running','local',0,0 FROM engine_meta WHERE singleton=1
            """, new("$id", Guid.NewGuid().ToString()), new("$folder", folder)));
        await CallAsync(bulk, 4, "commitSave", $$"""{"upload":"{{upload}}"}""");
        var refused = await ReadAsync(bulk);
        Assert.Equal("error", JsonDocument.Parse(refused).RootElement.GetProperty("t").GetString());
        Assert.Contains("is working in this folder", refused);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(folder, "a")));
        Assert.Empty(Directory.GetFiles(folder, ".assembly-save-*"));
    }

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
