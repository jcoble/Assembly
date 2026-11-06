using System.Text;
using System.Text.Json;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

/// <summary>The files calls over the socket with a path target, for a file outside every session's folder.</summary>
public sealed partial class FilesSocketTests
{
    private static JsonElement Frame(string answer) => JsonDocument.Parse(answer).RootElement;
    private static JsonElement Result(string answer) => Frame(answer).GetProperty("result");
    private static string? Code(string answer) => Frame(answer).GetProperty("code").GetString();
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static (string Root, string Path) OutsideFile(GitFolder folder, string text)
    {
        var path = Path.Combine(folder.Root, "a.txt");
        File.WriteAllText(path, text);
        return (Path.GetDirectoryName(FileOwner.RealFile(path))!, path);
    }

    [Fact]
    public async Task Owner_with_a_path_target_answers_the_outside_ownership()
    {
        using var folder = new GitFolder();
        var (root, path) = OutsideFile(folder, "one\n");
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "owner", $$$"""{"target":{"path":"{{{path}}}"}}""");
        var owner = Result(await ReadAsync(socket));
        Assert.Equal(_db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0), default), owner.GetProperty("machine").GetString());
        Assert.Equal(root, owner.GetProperty("checkoutRoot").GetString());
        Assert.Equal("", owner.GetProperty("remoteSessionId").GetString());
        Assert.Equal("a.txt", owner.GetProperty("file").GetString());
    }

    [Fact]
    public async Task Read_by_path_target_answers_the_file_s_text_and_revision()
    {
        using var folder = new GitFolder();
        var (_, path) = OutsideFile(folder, "one\ntwo\n");
        using var socket = await OpenAsync(bulk: true);
        await CallAsync(socket, 1, "read", $$$"""{"target":{"path":"{{{path}}}"}}""");
        var result = Result(await ReadAsync(socket));
        Assert.Equal("one\ntwo\n", result.GetProperty("content").GetString());
        Assert.Equal(await FileSave.RevisionAsync(path, default), result.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task Release_by_path_target_releases_that_read()
    {
        using var folder = new GitFolder();
        var (root, path) = OutsideFile(folder, new string('x', FileRead.ChunkBytes + 1));
        using var socket = await OpenAsync(bulk: true);
        await CallAsync(socket, 1, "read", $$$"""{"target":{"path":"{{{path}}}"}}""");
        var first = Result(await ReadAsync(socket));
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("nextOffset").ValueKind);
        var revision = first.GetProperty("revision").GetString();
        var machine = _db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0), default);
        var release = $$$"""{"target":{"path":"{{{path}}}"},"revision":"{{{revision}}}","expectedOwner":{"machine":"{{{machine}}}","checkoutRoot":"{{{root}}}","remoteSessionId":"","file":"a.txt"}}""";
        await CallAsync(socket, 2, "release", release);
        Assert.True(Result(await ReadAsync(socket)).GetProperty("released").GetBoolean());
        await CallAsync(socket, 3, "release", release);
        Assert.False(Result(await ReadAsync(socket)).GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task Begin_and_commit_by_path_target_saves_atomically()
    {
        using var folder = new GitFolder();
        var (root, path) = OutsideFile(folder, "old");
        using var socket = await OpenAsync(bulk: true);
        var revision = await FileSave.RevisionAsync(path, default);
        await CallAsync(socket, 1, "beginSave", $$$"""{"target":{"path":"{{{path}}}"},"expectedRevision":"{{{revision}}}","size":3}""");
        var upload = Result(await ReadAsync(socket)).GetProperty("upload").GetString();
        Assert.Single(Directory.GetFiles(root, ".assembly-save-*"));
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        await CallAsync(socket, 2, "saveChunk", $$$"""{"upload":"{{{upload}}}","offset":0,"data":"{{{Base64("new")}}}"}""");
        await ReadAsync(socket);
        await CallAsync(socket, 3, "commitSave", $$$"""{"upload":"{{{upload}}}"}""");
        var saved = Result(await ReadAsync(socket));
        Assert.Equal("saved", saved.GetProperty("state").GetString());
        Assert.NotEqual(revision, saved.GetProperty("revision").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, ".assembly-save-*"));
    }

    [Fact]
    public async Task A_changed_file_is_a_conflict_by_path_target_too()
    {
        using var folder = new GitFolder();
        var (_, path) = OutsideFile(folder, "old");
        using var socket = await OpenAsync(bulk: true);
        var revision = await FileSave.RevisionAsync(path, default);
        await CallAsync(socket, 1, "beginSave", $$$"""{"target":{"path":"{{{path}}}"},"expectedRevision":"{{{revision}}}","size":3}""");
        var upload = Result(await ReadAsync(socket)).GetProperty("upload").GetString();
        await CallAsync(socket, 2, "saveChunk", $$$"""{"upload":"{{{upload}}}","offset":0,"data":"{{{Base64("new")}}}"}""");
        await ReadAsync(socket);
        await File.WriteAllTextAsync(path, "theirs");
        await CallAsync(socket, 3, "commitSave", $$$"""{"upload":"{{{upload}}}"}""");
        var result = Result(await ReadAsync(socket));
        Assert.Equal("conflict", result.GetProperty("state").GetString());
        Assert.Equal("revision_changed", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("theirs", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_call_with_both_or_neither_target_is_bad_arguments()
    {
        using var folder = new GitFolder();
        var (_, path) = OutsideFile(folder, "old");
        var session = _sessions.Create("claude", "https://github.com/o/r");
        Directory.CreateDirectory(_sessions.Item(session).Root!);
        var both = $"\"session\":\"{session.SessionId}\",\"target\":{{\"path\":\"{path}\"}}";
        var revision = new string('0', 64);
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        foreach (var args in new[] { $$$"""{{{{both}}}}""", "{}" })
        {
            await CallAsync(normal, 1, "owner", args);
            Assert.Equal("bad_args", Code(await ReadAsync(normal)));
        }
        foreach (var (method, args) in new[]
        {
            ("read", $$$"""{{{{both}}}}"""),
            ("read", """{"path":"a.txt"}"""),
            ("read", $$$"""{"target":{"path":"{{{path}}}"},"path":"a.txt"}"""),
            ("read", $$$"""{"session":"{{{session.SessionId}}}"}"""),
            ("release", $$$"""{{{{both}}},"revision":"{{{revision}}}"}"""),
            ("release", $$$"""{"path":"a.txt","revision":"{{{revision}}}"}"""),
            ("release", $$$"""{"target":{"path":"{{{path}}}"},"path":"a.txt","revision":"{{{revision}}}"}"""),
            ("release", $$$"""{"target":{"path":"{{{path}}}"},"revision":"{{{revision}}}"}"""),
            ("release", $$$"""{"session":"{{{session.SessionId}}}","revision":"{{{revision}}}"}"""),
            ("beginSave", $$$"""{{{{both}}},"expectedRevision":"{{{revision}}}","size":0}"""),
            ("beginSave", $$$"""{"path":"a.txt","expectedRevision":"{{{revision}}}","size":0}"""),
            ("beginSave", $$$"""{"target":{"path":"{{{path}}}"},"path":"a.txt","expectedRevision":"{{{revision}}}","size":0}"""),
            ("beginSave", $$$"""{"session":"{{{session.SessionId}}}","expectedRevision":"{{{revision}}}","size":0}"""),
        })
        {
            await CallAsync(bulk, 2, method, args);
            var answer = await ReadAsync(bulk);
            Assert.True(Code(answer) == "bad_args", $"{method} {args}: {answer}");
        }
        Assert.Empty(Directory.GetFiles(folder.Root, ".assembly-save-*"));
    }

    [Fact]
    public async Task List_search_observe_and_action_refuse_a_path_target()
    {
        using var folder = new GitFolder();
        var (_, path) = OutsideFile(folder, "old");
        var session = _sessions.Create("claude", "https://github.com/o/r");
        Directory.CreateDirectory(_sessions.Item(session).Root!);
        var target = $"\"target\":{{\"path\":\"{path}\"}}";
        var named = $"\"session\":\"{session.SessionId}\",{target}";
        using var socket = await OpenAsync();
        foreach (var (method, args) in new[]
        {
            ("list", $$$"""{{{{target}}}}"""),
            ("list", $$$"""{{{{named}}}}"""),
            ("search", $$$"""{{{{target}}},"query":"a"}"""),
            ("search", $$$"""{{{{named}}},"query":"a"}"""),
            ("observe", $$$"""{{{{target}}}}"""),
            ("observe", $$$"""{{{{named}}}}"""),
            ("action", $$$"""{{{{target}}},"kind":"trash","path":"a.txt"}"""),
            ("action", $$$"""{{{{named}}},"kind":"trash","path":"a.txt"}"""),
        })
        {
            await CallAsync(socket, 1, method, args);
            var answer = await ReadAsync(socket);
            Assert.True(Code(answer) == "bad_args", $"{method} {args}: {answer}");
        }
        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_path_target_with_an_expected_source_captures_no_session_source()
    {
        using var folder = new GitFolder();
        var (_, path) = OutsideFile(folder, "one\n");
        using var socket = await OpenAsync(bulk: true);
        await CallAsync(socket, 1, "read", $$$"""{"target":{"path":"{{{path}}}"},"expectedSource":{"machine":"m","generation":1}}""");
        Assert.Equal("one\n", Result(await ReadAsync(socket)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Session_target_calls_are_unchanged()
    {
        var session = _sessions.Create("claude", "https://github.com/o/r");
        var root = _sessions.Item(session).Root!;
        GitFolder.Fill(root, "src/");
        var path = Path.Combine(root, "src", "a.txt");
        await File.WriteAllTextAsync(path, "old");
        using var normal = await OpenAsync();
        using var socket = await OpenAsync(bulk: true);
        await CallAsync(normal, 1, "owner", $$$"""{"session":"{{{session.SessionId}}}"}""");
        var owner = Result(await ReadAsync(normal));
        Assert.Equal(session.SessionId, owner.GetProperty("remoteSessionId").GetString());
        Assert.Equal(JsonValueKind.Null, owner.GetProperty("file").ValueKind);
        await CallAsync(socket, 2, "read", $$$"""{"session":"{{{session.SessionId}}}","path":"src/a.txt"}""");
        var read = Result(await ReadAsync(socket));
        Assert.Equal("old", read.GetProperty("content").GetString());
        await CallAsync(socket, 3, "beginSave", $$$"""{"session":"{{{session.SessionId}}}","path":"src/a.txt","expectedRevision":"{{{read.GetProperty("revision").GetString()}}}","size":3}""");
        var upload = Result(await ReadAsync(socket)).GetProperty("upload").GetString();
        await CallAsync(socket, 4, "saveChunk", $$$"""{"upload":"{{{upload}}}","offset":0,"data":"{{{Base64("new")}}}"}""");
        await ReadAsync(socket);
        await CallAsync(socket, 5, "commitSave", $$$"""{"upload":"{{{upload}}}"}""");
        Assert.Equal("saved", Result(await ReadAsync(socket)).GetProperty("state").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(path));
        await CallAsync(socket, 6, "read", $$$"""{"session":"{{{session.SessionId}}}","path":"/etc/hostname"}""");
        Assert.Contains("expected a relative file path", await ReadAsync(socket));
    }
}
