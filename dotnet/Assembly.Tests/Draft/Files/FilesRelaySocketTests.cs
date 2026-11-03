using System.Text.Json;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed partial class FilesSocketTests
{
    [Fact]
    public async Task Closing_one_scope_removes_only_its_staged_upload_on_the_shared_bulk_socket()
    {
        var session = _sessions.Create("codex", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a"), "old");
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(bulk, 1, "read", $$"""{"session":"{{session.SessionId}}","path":"a"}""");
        var revision = JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("revision").GetString();
        var scopes = new List<string>();
        var uploads = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await CallAsync(normal, 10 + i, "openScope", "{}");
            var grant = JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("result");
            var scope = grant.GetProperty("scope").GetString()!;
            scopes.Add(scope);
            await CallAsync(bulk, 20 + i, "attachScope", grant.GetRawText());
            Assert.Equal("result", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("t").GetString());
            await CallAsync(bulk, 30 + i, "beginSave", $$"""{"session":"{{session.SessionId}}","path":"a","expectedRevision":"{{revision}}","size":3,"relayScope":"{{scope}}"}""");
            uploads.Add(JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("upload").GetString()!);
        }
        Assert.Equal(uploads.Select(id => Path.Combine(root, ".assembly-save-" + id)).Order(),
            Directory.GetFiles(root, ".assembly-save-*").Order());
        await CallAsync(normal, 40, "closeScope", $$"""{"scope":"{{scopes[0]}}"}""");
        Assert.True(JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("result").GetProperty("released").GetBoolean());
        Assert.False(File.Exists(Path.Combine(root, ".assembly-save-" + uploads[0])));
        Assert.True(File.Exists(Path.Combine(root, ".assembly-save-" + uploads[1])));
        await CallAsync(bulk, 41, "saveChunk", $$"""{"upload":"{{uploads[0]}}","offset":0,"data":"bmV3","relayScope":"{{scopes[1]}}"}""");
        Assert.Contains("unknown save upload", await ReadAsync(bulk));
        await CallAsync(bulk, 42, "saveChunk", $$"""{"upload":"{{uploads[1]}}","offset":0,"data":"bmV3","relayScope":"{{scopes[1]}}"}""");
        Assert.Equal("result", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("t").GetString());
        await CallAsync(bulk, 43, "commitSave", $$"""{"upload":"{{uploads[1]}}","relayScope":"{{scopes[1]}}"}""");
        Assert.Equal("saved", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result").GetProperty("state").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root, "a")));
        Assert.Empty(Directory.GetFiles(root, ".assembly-save-*"));
    }

    [Fact]
    public async Task Scoped_read_requires_bulk_attachment_and_normal_close_refuses_a_late_continuation()
    {
        var session = _sessions.Create("codex", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a"), new string('x', FileRead.ChunkBytes + 1));
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        await CallAsync(normal, 1, "openScope", "{}");
        var grant = JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("result");
        var scope = grant.GetProperty("scope").GetString();
        var attachment = grant.GetProperty("attachment").GetString();
        var read = $$"""{"session":"{{session.SessionId}}","path":"a","relayScope":"{{scope}}"}""";
        await CallAsync(bulk, 2, "read", read);
        Assert.Contains("file relay scope changed", await ReadAsync(bulk));
        await CallAsync(bulk, 3, "attachScope", $$"""{"scope":"{{scope}}","attachment":"{{attachment}}"}""");
        Assert.Equal("result", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("t").GetString());
        await CallAsync(bulk, 4, "read", read);
        var first = JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("result");
        Assert.True(first.GetProperty("nextOffset").GetInt64() > 0);
        await CallAsync(normal, 5, "closeScope", $$"""{"scope":"{{scope}}"}""");
        Assert.True(JsonDocument.Parse(await ReadAsync(normal)).RootElement.GetProperty("result").GetProperty("released").GetBoolean());
        await CallAsync(bulk, 6, "read", $$"""{"session":"{{session.SessionId}}","path":"a","offset":{{first.GetProperty("nextOffset").GetInt64()}},"revision":"{{first.GetProperty("revision").GetString()}}","relayScope":"{{scope}}"}""");
        Assert.Contains("file relay scope changed", await ReadAsync(bulk));
        await CallAsync(bulk, 7, "read", $$"""{"session":"{{session.SessionId}}","path":"a"}""");
        Assert.Equal("result", JsonDocument.Parse(await ReadAsync(bulk)).RootElement.GetProperty("t").GetString());
    }
}
