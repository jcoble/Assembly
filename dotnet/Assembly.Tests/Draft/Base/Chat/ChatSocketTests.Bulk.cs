using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Draft.Base.Chat;

// The window's second socket, /socket/bulk, carries the large calls (attachments, rows/page and rows/tool), so a big
// upload or page never holds up the main socket's calls, topics and live rows.
public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task A_call_on_the_wrong_socket_answers_not_found()
    {
        var session = _sessions.Create("claude", "repo");
        using var main = await OpenAsync();
        using var bulk = await OpenBulkAsync();

        await CallAsync(main, 1, "attachments", "begin", $$"""{"session":"{{session.SessionId}}","mime":"image/png","size":10}""");
        Assert.Equal("""{"t":"error","id":1,"code":"not_found","message":"attachments/begin is on /socket/bulk"}""", await ReadAsync(main));
        await CallAsync(bulk, 2, "config", "get", $$"""{"session":"{{session.SessionId}}"}""");
        Assert.Equal("""{"t":"error","id":2,"code":"not_found","message":"config/get is on /socket"}""", await ReadAsync(bulk));
        await SendAsync(bulk, $$"""{"t":"sub","id":3,"topic":"rows","key":"{{session.SessionId}}"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"not_found","message":"there are no topics on /socket/bulk"}""", await ReadAsync(bulk));
    }

    [Fact]
    public async Task Main_answers_and_streams_rows_while_a_20_MB_upload_is_held_on_bulk()
    {
        var remote = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var local = _sessions.Create("claude", "repo");
        var image = Png(AttachmentStore.MaxBytes);
        using var main = await OpenAsync();
        using var bulk = await OpenBulkAsync();
        var upload = await UploadAsync(bulk, remote.SessionId, image);
        _box.Hold();
        await CallAsync(bulk, 3, "attachments", "commit", $$"""{"upload":"{{upload}}"}""");
        await _box.Reached.Task.WaitAsync(Wait);

        await SendAsync(main, $$"""{"t":"sub","id":1,"topic":"rows","key":"{{local.SessionId}}"}""");
        Assert.StartsWith("""{"t":"state","sub":1,""", await ReadAsync(main));
        Row(local.SessionId, RowKind.Message, "m1", """{"text":"hi"}""");
        Assert.StartsWith("""{"t":"rows","sub":1,""", await ReadAsync(main));
        await CallAsync(main, 2, "config", "get", $$"""{"session":"{{local.SessionId}}"}""");
        Assert.StartsWith("""{"t":"result","id":2,""", await ReadAsync(main));

        _box.Release();
        Assert.Equal("""{"t":"result","id":3,"result":{"path":"/box/image.png"}}""", await ReadAsync(bulk));
        Assert.Equal(image, _box.Saved);
    }

    // A commit queued behind another when the socket closes never reaches the machine: the window that started it is
    // gone, and its reconnect starts a fresh upload.
    [Fact]
    public async Task A_bulk_socket_that_closes_mid_upload_leaves_no_staging_file_and_no_save()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        var other = _sessions.Create("claude", "repo");
        var first = Png(100);
        var bulk = await OpenBulkAsync();
        var a = await UploadAsync(bulk, session.SessionId, first);
        var b = await UploadAsync(bulk, session.SessionId, Png(200));
        _here.Hold();
        _box.Hold();
        // A read is cancelled by its socket closing, so once this one is, the backend has seen the close.
        await CallAsync(bulk, 2, "rows", "tool", $$"""{"session":"{{other.SessionId}}","item":"t1"}""");
        await _here.Reached.Task.WaitAsync(Wait);
        await CallAsync(bulk, 3, "attachments", "commit", $$"""{"upload":"{{a}}"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(bulk, 4, "attachments", "commit", $$"""{"upload":"{{b}}"}""");
        // Frames are read in order, so once this drop of no upload answers, B's commit is queued behind A's.
        await CallAsync(bulk, 5, "attachments", "drop", """{"upload":"nope"}""");
        Assert.Equal([5], (await ReadAnswersAsync(bulk, 1)).Keys);

        bulk.Abort();
        bulk.Dispose();
        await _here.Cancelled.Task.WaitAsync(Wait);
        _box.Release();
        await _socketClosed.Task.WaitAsync(Wait);

        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Equal([$"save png {first.Length}"], _box.Asked);
        Assert.Equal(first, _box.Saved);
    }

    [Fact]
    public async Task A_bulk_socket_that_closes_after_one_chunk_leaves_no_staging_file()
    {
        var session = _sessions.Create("claude", "repo");
        var bulk = await OpenBulkAsync();
        var upload = await BeginAsync(bulk, session.SessionId, "image/png", 2 * Mb);
        await ChunkAsync(bulk, 2, upload, 0, Png(Mb));
        Result(await ReadAsync(bulk));

        bulk.Abort();
        bulk.Dispose();
        await _socketClosed.Task.WaitAsync(Wait);

        Assert.Empty(Directory.GetFiles(Staging));
    }
}
