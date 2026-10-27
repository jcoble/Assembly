using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Machines;
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

    // The socket's close drops what it left only once the chunks it queued have run: here they wait behind a send that
    // hangs on the session's machine, past the close.
    [Fact]
    public async Task Closing_the_socket_drops_its_uploads_after_their_chunks()
    {
        var session = _sessions.Create("claude", "repo");
        var other = _sessions.Create("claude", "repo");
        var socket = await OpenAsync();
        var upload = await BeginAsync(socket, session.SessionId, "image/png", 4 * Mb);
        _here.Hold();

        // A read is cancelled by its socket closing, and a closing socket cancels in the reverse order the calls came,
        // so once this one, sent first, is cancelled, the backend is closing.
        await CallAsync(socket, 2, "rows", "tool", $$"""{"session":"{{other.SessionId}}","item":"t1"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 3, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");
        for (var i = 0; i < 3; i++)
            await ChunkAsync(socket, 10 + i, upload, i * Mb, new byte[Mb]);
        // Frames are read in order, so once this save answers, the send and the chunks are queued.
        await CallAsync(socket, 20, "preferences", "set", Save);
        Assert.Equal([20], (await ReadAnswersAsync(socket, 1)).Keys);
        socket.Abort();
        socket.Dispose();
        await _here.Cancelled.Task.WaitAsync(Wait);

        // Nothing marks the moment a close would drop it too early, so give that half a second to show.
        Assert.False(SpinWait.SpinUntil(() => Directory.GetFiles(Staging).Length == 0, TimeSpan.FromMilliseconds(500)));
        _here.Release();
        Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(Staging).Length == 0, Wait));
    }

    [Fact]
    public async Task Drop_deletes_an_uploads_staging_file_and_answers_ok_for_one_it_doesnt_know()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();
        var upload = await BeginAsync(socket, session.SessionId, "image/png", 10);

        await CallAsync(socket, 2, "attachments", "drop", $$"""{"upload":"{{upload}}"}""");
        Assert.Equal("""{"t":"result","id":2,"result":{"ok":true}}""", await ReadAsync(socket));
        Assert.Empty(Directory.GetFiles(Staging));
        await ChunkAsync(socket, 3, upload, 0, new byte[5]);
        Assert.Contains("unknown upload", await ReadAsync(socket));

        // Dropped already, and never begun.
        foreach (var id in new[] { upload, "nope" })
        {
            await CallAsync(socket, 4, "attachments", "drop", $$"""{"upload":"{{id}}"}""");
            Assert.Equal("""{"t":"result","id":4,"result":{"ok":true}}""", await ReadAsync(socket));
        }
    }

    [Fact]
    public async Task A_ninth_open_upload_fails_until_one_is_dropped()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();
        var uploads = new List<string>();
        for (var i = 0; i < 8; i++)
            uploads.Add(await BeginAsync(socket, session.SessionId, "image/png", 10));

        await CallAsync(socket, 1, "attachments", "begin", $$"""{"session":"{{session.SessionId}}","mime":"image/png","size":10}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        Assert.Equal(8, Directory.GetFiles(Staging).Length);

        await CallAsync(socket, 2, "attachments", "drop", $$"""{"upload":"{{uploads[0]}}"}""");
        await ReadAsync(socket);
        await BeginAsync(socket, session.SessionId, "image/png", 10);
    }

    // The chunk and the drop wait behind a send that hangs on the session's machine; if the drop ran first, the chunk
    // would find no upload.
    [Fact]
    public async Task A_drop_queued_behind_chunks_runs_after_them()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();
        var upload = await BeginAsync(socket, session.SessionId, "image/png", 10);
        _here.Hold();
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await ChunkAsync(socket, 3, upload, 0, new byte[5]);
        await CallAsync(socket, 4, "attachments", "drop", $$"""{"upload":"{{upload}}"}""");

        _here.Release();
        var answers = await ReadAnswersAsync(socket, 3);

        Assert.Equal("""{"t":"result","id":3,"result":{"bytes":5}}""", answers[3]);
        Assert.Equal("""{"t":"result","id":4,"result":{"ok":true}}""", answers[4]);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    // A cancelled chunk never runs, but its upload goes in its turn, with no further call: a window that cancels and
    // never drops isn't left at the cap.
    [Fact]
    public async Task A_chunk_cancelled_in_the_queue_drops_its_upload()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();
        var uploads = new List<string>();
        for (var i = 0; i < 8; i++)
            uploads.Add(await BeginAsync(socket, session.SessionId, "image/png", 10));
        _here.Hold();
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{session.SessionId}}","text":"hi"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await ChunkAsync(socket, 3, uploads[0], 0, new byte[5]);
        await SendAsync(socket, """{"t":"cancel","id":3}""");
        // Frames are read in order, so once this save answers, the cancel has landed.
        await CallAsync(socket, 4, "preferences", "set", Save);
        Assert.Equal([4], (await ReadAnswersAsync(socket, 1)).Keys);

        _here.Release();
        var answers = await ReadAnswersAsync(socket, 2);

        Assert.Contains("\"code\":\"cancelled\"", answers[3]);
        Assert.Equal(7, Directory.GetFiles(Staging).Length);
        await BeginAsync(socket, session.SessionId, "image/png", 10);
    }

    // Only what begin makes goes: a --home that points somewhere real loses nothing else.
    [Fact]
    public void A_start_clears_only_leftover_staging_files()
    {
        Directory.CreateDirectory(Staging);
        var left = Path.Combine(Staging, Guid.NewGuid().ToString());
        var other = Path.Combine(Staging, "notes.txt");
        var folder = Directory.CreateDirectory(Path.Combine(Staging, "folder"));
        var inside = Path.Combine(folder.FullName, Guid.NewGuid().ToString());
        foreach (var file in new[] { left, other, inside })
            File.WriteAllText(file, "x");

        _ = new AttachmentsFeature(_sessions, new MachineSet(_here, _box), _attachments, Staging);

        Assert.False(File.Exists(left));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(inside));
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
