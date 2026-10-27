using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    private const int Mb = 1024 * 1024;

    [Fact]
    public async Task An_image_sent_in_chunks_is_saved_on_the_sessions_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var image = Png(5 * Mb / 2);
        using var socket = await OpenAsync();

        var upload = await BeginAsync(socket, session.SessionId, "image/png", image.Length);
        // The staging file is under the backend's home.
        Assert.Single(Directory.GetFiles(Staging));
        for (var offset = 0; offset < image.Length; offset += Mb)
        {
            await ChunkAsync(socket, 2, upload, offset, image.AsSpan(offset, Math.Min(Mb, image.Length - offset)).ToArray());
            Assert.Equal(Math.Min(offset + Mb, image.Length), Result(await ReadAsync(socket)).GetProperty("bytes").GetInt64());
        }
        await CallAsync(socket, 3, "attachments", "commit", $$"""{"upload":"{{upload}}"}""");

        Assert.Equal("""{"t":"result","id":3,"result":{"path":"/box/image.png"}}""", await ReadAsync(socket));
        Assert.Equal(image, _box.Saved);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task Begin_turns_down_a_type_size_or_session_it_cant_take()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        foreach (var (mime, size, id) in new[] { ("text/plain", 10L, session.SessionId), ("image/png", AttachmentStore.MaxBytes + 1L, session.SessionId),
                     ("image/png", 0L, session.SessionId), ("image/png", 10L, "nope") })
        {
            await CallAsync(socket, 1, "attachments", "begin", $$"""{"session":"{{id}}","mime":"{{mime}}","size":{{size}}}""");
            Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        }
        Assert.False(Directory.Exists(Staging) && Directory.GetFiles(Staging).Length > 0);
    }

    [Fact]
    public async Task A_chunk_out_of_place_or_too_big_fails_and_drops_its_upload()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        // At the wrong offset; after it, the upload is gone.
        var upload = await BeginAsync(socket, session.SessionId, "image/png", 10);
        await ChunkAsync(socket, 2, upload, 5, new byte[5]);
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await ChunkAsync(socket, 3, upload, 0, new byte[5]);
        Assert.Contains("unknown upload", await ReadAsync(socket));

        // Past the size begin declared.
        upload = await BeginAsync(socket, session.SessionId, "image/png", 4);
        await ChunkAsync(socket, 2, upload, 0, new byte[5]);
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));

        // Over the chunk limit.
        upload = await BeginAsync(socket, session.SessionId, "image/png", 2 * Mb);
        await ChunkAsync(socket, 2, upload, 0, new byte[Mb + 1]);
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));

        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task Commit_turns_down_bytes_that_arent_the_type_sent_or_arent_all_there()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        var upload = await BeginAsync(socket, session.SessionId, "image/png", 5);
        await ChunkAsync(socket, 2, upload, 0, "hello"u8.ToArray());
        await ReadAsync(socket);
        await CallAsync(socket, 3, "attachments", "commit", $$"""{"upload":"{{upload}}"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"failed","message":"the bytes are not the image type sent"}""", await ReadAsync(socket));

        upload = await BeginAsync(socket, session.SessionId, "image/png", 100);
        await ChunkAsync(socket, 2, upload, 0, Png(50));
        await ReadAsync(socket);
        await CallAsync(socket, 3, "attachments", "commit", $$"""{"upload":"{{upload}}"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));

        Assert.Empty(_here.Asked);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task An_upload_belongs_to_the_socket_that_began_it()
    {
        var session = _sessions.Create("claude", "repo");
        using var mine = await OpenAsync();
        using var other = await OpenAsync();

        var upload = await BeginAsync(mine, session.SessionId, "image/png", 10);
        await ChunkAsync(other, 2, upload, 0, new byte[5]);

        Assert.Contains("unknown upload", await ReadAsync(other));
    }

    // The socket's close drops what it left, once its chunks have run: no chunk writes to a file already deleted.
    [Fact]
    public async Task Closing_the_socket_drops_its_uploads_after_their_chunks()
    {
        var session = _sessions.Create("claude", "repo");
        var socket = await OpenAsync();

        var upload = await BeginAsync(socket, session.SessionId, "image/png", 4 * Mb);
        for (var i = 0; i < 3; i++)
            await ChunkAsync(socket, 10 + i, upload, i * Mb, new byte[Mb]);
        // Frames are read in order, so once this save answers, the chunks are queued.
        await CallAsync(socket, 20, "preferences", "set", Save);
        Assert.Contains(20L, (await ReadAnswersAsync(socket, 4)).Keys);
        socket.Abort();
        socket.Dispose();

        Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(Staging).Length == 0, Wait));
    }

    [Fact]
    public async Task Read_answers_an_attachment_a_piece_at_a_time()
    {
        var session = _sessions.Create("claude", "repo");
        var image = Png(3 * Mb / 2);
        var name = Path.GetFileName(await _attachments.SaveAsync(session.SessionId, AttachmentImage, image, CancellationToken.None));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}"}""");
        var first = Result(await ReadAsync(socket));
        await CallAsync(socket, 2, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"{{name}}","offset":{{Mb}}}""");
        var rest = Result(await ReadAsync(socket));

        Assert.Equal(image, first.GetProperty("data").GetBytesFromBase64().Concat(rest.GetProperty("data").GetBytesFromBase64()).ToArray());
        Assert.Equal(image.Length, first.GetProperty("size").GetInt64());
        Assert.Equal("image/png", first.GetProperty("mime").GetString());
        await CallAsync(socket, 3, "attachments", "read", $$"""{"session":"{{session.SessionId}}","name":"../secret.png"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    private static readonly Assembly.Application.Features.Chat.Attachments.ImageType AttachmentImage =
        Assembly.Application.Features.Chat.Attachments.ImageType.ForExtension("png")!;

    // A PNG's first bytes, then filler.
    private static byte[] Png(int size)
    {
        var bytes = new byte[size];
        AttachmentImage.Magic.CopyTo(bytes, 0);
        for (var i = AttachmentImage.Magic.Length; i < size; i++)
            bytes[i] = (byte)i;
        return bytes;
    }

    private static async Task<string> BeginAsync(WebSocket socket, string session, string mime, long size)
    {
        await CallAsync(socket, 1, "attachments", "begin", $$"""{"session":"{{session}}","mime":"{{mime}}","size":{{size}}}""");
        return Result(await ReadAsync(socket)).GetProperty("upload").GetString()!;
    }

    private static Task ChunkAsync(WebSocket socket, long id, string upload, long offset, byte[] data) =>
        CallAsync(socket, id, "attachments", "chunk", $$"""{"upload":"{{upload}}","offset":{{offset}},"data":"{{Convert.ToBase64String(data)}}"}""");

    private static JsonElement Result(string frame)
    {
        using var json = JsonDocument.Parse(frame);
        Assert.True(json.RootElement.TryGetProperty("result", out var result), frame);
        return result.Clone();
    }
}
