using System.Text.Json;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Attachment_read_uses_remote_owner_bytes_and_never_a_matching_desktop_image()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var owner = new AttachmentStore(Path.Combine(_home.FullName, "owner-images"));
        _box.Attachments = owner;
        var image = Png(Mb + 17);
        var path = await owner.SaveAsync(session.SessionId, AttachmentImage, image, default);
        var name = Path.GetFileName(path);
        var other = image.ToArray(); other[^1] ^= 0x80;
        var desktop = _attachments.PathOf(session.SessionId, name);
        Directory.CreateDirectory(Path.GetDirectoryName(desktop)!); await File.WriteAllBytesAsync(desktop, other);
        using var socket = await OpenBulkAsync();
        await CallAsync(socket, 1, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}"}""");
        var first = Result(await ReadAsync(socket));
        await CallAsync(socket, 2, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}","offset":{{Mb}}}""");
        var rest = Result(await ReadAsync(socket));
        Assert.Equal(image, first.GetProperty("data").GetBytesFromBase64().Concat(rest.GetProperty("data").GetBytesFromBase64()).ToArray());
        Assert.Equal(Mb, first.GetProperty("data").GetBytesFromBase64().Length);
        Assert.Equal(image.LongLength, rest.GetProperty("size").GetInt64()); Assert.Equal("image/png", rest.GetProperty("mime").GetString());
        Assert.Equal(2, _box.Asked.Count); Assert.Empty(_here.Asked);
        File.Delete(path);
        await CallAsync(socket, 3, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}"}""");
        var missing = JsonDocument.Parse(await ReadAsync(socket)).RootElement;
        Assert.Equal("failed", missing.GetProperty("code").GetString()); Assert.Equal("no such attachment", missing.GetProperty("message").GetString());
        Assert.True(File.Exists(desktop));
    }

    [Fact]
    public async Task Attachment_read_on_a_disconnected_remote_does_not_use_the_desktop_copy()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var path = await _attachments.SaveAsync(session.SessionId, AttachmentImage, Png(100), default);
        _box.Down = true;
        using var socket = await OpenBulkAsync();
        await CallAsync(socket, 1, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{Path.GetFileName(path)}}"}""");
        var result = JsonDocument.Parse(await ReadAsync(socket)).RootElement;
        Assert.Equal("error", result.GetProperty("t").GetString());
        Assert.Equal("unreachable", result.GetProperty("code").GetString()); Assert.Equal("box", result.GetProperty("machine").GetString());
        Assert.True(File.Exists(path)); Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task Cancelling_attachment_read_reaches_its_owning_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var path = await _attachments.SaveAsync(session.SessionId, AttachmentImage, Png(100), default);
        _box.Hold();
        using var socket = await OpenBulkAsync();
        await CallAsync(socket, 1, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{Path.GetFileName(path)}}"}""");
        var answer = ReadAsync(socket);
        Assert.Same(_box.Reached.Task, await Task.WhenAny(_box.Reached.Task, answer).WaitAsync(Wait));
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Equal("cancelled", JsonDocument.Parse(await answer).RootElement.GetProperty("code").GetString());
        Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task Attachment_read_preserves_owner_EOF_name_offset_and_session_guards()
    {
        _here.Attachments = _attachments;
        var session = _sessions.Create("claude", "repo");
        var image = Png(128);
        var path = await _attachments.SaveAsync(session.SessionId, AttachmentImage, image, default);
        var name = Path.GetFileName(path);
        using var socket = await OpenBulkAsync();
        await CallAsync(socket, 1, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}","offset":128}""");
        var eof = Result(await ReadAsync(socket));
        Assert.Empty(eof.GetProperty("data").GetBytesFromBase64()); Assert.Equal(128, eof.GetProperty("size").GetInt64()); Assert.Equal("image/png", eof.GetProperty("mime").GetString());
        foreach (var offset in new long[] { -1, 129 })
        {
            await CallAsync(socket, 2, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}","offset":{{offset}}}""");
            Assert.Equal("the offset is outside the attachment", JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("message").GetString());
        }
        await CallAsync(socket, 3, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"../secret.png"}""");
        Assert.Equal("not an attachment name", JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("message").GetString());
        await CallAsync(socket, 4, "attachments", "read", $$"""{"session":"missing","name":"{{name}}"}""");
        Assert.Equal("no such attachment", JsonDocument.Parse(await ReadAsync(socket)).RootElement.GetProperty("message").GetString());
        Assert.Equal(4, _here.Asked.Count); Assert.Empty(_box.Asked);
    }
}
