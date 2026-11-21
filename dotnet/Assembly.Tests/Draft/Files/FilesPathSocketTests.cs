using System.Text;
using System.Text.Json;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

/// <summary>The files calls over the socket with a path target, for a file outside every session's folder.</summary>
public sealed partial class FilesSocketTests
{
    private static JsonElement Frame(string answer) => JsonDocument.Parse(answer).RootElement;
    private static JsonElement Result(string answer) => Frame(answer).GetProperty("result");
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static (string Root, string Path) OutsideFile(GitFolder folder, string text)
    {
        var path = Path.Combine(folder.Root, "a.txt");
        File.WriteAllText(path, text);
        return (Path.GetDirectoryName(FileOwner.RealFile(path))!, path);
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
}
