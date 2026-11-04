using Assembly.Application.Features.Notion;
using Assembly.Infrastructure.Notion;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionEditTests
{
    [Fact]
    public async Task Forbidden_status_does_not_change_cached_status()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        f.Database.Writer.Write("test.seed", s => cache.Replace(s, f.Settings.Read().Generation, [NotionCacheTests.Row(1) with { PageId = NotionFixture.Page, Status = "To Do" }], 10, false));
        QueueEditablePage(f);
        f.Enqueue("/v1/pages/" + NotionFixture.Page, "{}", 403);
        var edits = new NotionEdits(f.Database, f.Settings, f.Http);
        Assert.Equal("notion_http_403", (await Assert.ThrowsAsync<NotionException>(() => edits.PropertyAsync(new(NotionFixture.Source, NotionFixture.Page), "Status", "Done", "r1", default))).Code);
        Assert.Equal("To Do", Assert.Single(cache.Query(new(), default).Rows).Status);
    }
    [Fact]
    public async Task Stale_revision_fails_before_mutation()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueueEditablePage(f);
        Assert.Equal("notion_changed", (await Assert.ThrowsAsync<NotionException>(() => new NotionEdits(f.Database, f.Settings, f.Http).PropertyAsync(new(NotionFixture.Source, NotionFixture.Page), "Status", "Done", "old", default))).Code);
        Assert.DoesNotContain(f.Handler.Requests, x => x.Method == "PATCH");
    }
    [Fact]
    public async Task Lost_append_response_returns_uncertain_without_automatic_retry()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueueEditablePage(f);
        f.Handler.LoseNextPatch = true;
        var receipt = await new NotionEdits(f.Database, f.Settings, f.Http).AppendTextAsync(new(NotionFixture.Source, NotionFixture.Page), "unique marker", default);
        Assert.Equal("uncertain", receipt.State);
        Assert.Single(f.Handler.Requests, x => x.Method == "PATCH");
    }
    [Fact]
    public async Task Unsupported_block_cannot_send_a_mutation()
    {
        await using var f = new NotionFixture();
        Assert.Equal("notion_unsupported_body", (await Assert.ThrowsAsync<NotionException>(() => new NotionEdits(f.Database, f.Settings, f.Http).BlockAsync(new(NotionFixture.Source, NotionFixture.Page), NotionFixture.Other, "table", "x", null, "r1", default))).Code);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task Image_mime_mismatch_removes_owned_staging_without_network()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var root = Path.Combine(Path.GetTempPath(), "notion-image-test-" + Guid.NewGuid());
        using var images = new NotionImageUpload(root, f.Http, f.Settings, new NotionEdits(f.Database, f.Settings, f.Http));
        var lease = await images.BeginAsync("connection", new(NotionFixture.Source, NotionFixture.Page), "image/png", 8, default);
        await images.ChunkAsync("connection", lease.UploadId, 0, Convert.ToBase64String("GIF89abc"u8), default);
        Assert.Equal("image_type_mismatch", (await Assert.ThrowsAsync<NotionException>(() => images.CommitAsync("connection", lease.UploadId, default))).Code);
        Assert.Empty(Directory.GetFiles(root)); Assert.Empty(f.Handler.Requests);
        Directory.Delete(root);
    }

    [Fact]
    public async Task Upload_connection_close_deletes_only_its_owned_files_and_rejects_late_chunks()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var root = Path.Combine(Path.GetTempPath(), "notion-image-test-" + Guid.NewGuid());
        using var images = new NotionImageUpload(root, f.Http, f.Settings, new NotionEdits(f.Database, f.Settings, f.Http));
        var a = await images.BeginAsync("a", new(NotionFixture.Source, NotionFixture.Page), "image/png", 8, default);
        await images.BeginAsync("b", new(NotionFixture.Source, NotionFixture.Page), "image/png", 8, default);
        images.Close("a");
        Assert.Single(Directory.GetFiles(root));
        Assert.Equal("unknown_upload", (await Assert.ThrowsAsync<NotionException>(() => images.ChunkAsync("a", a.UploadId, 0, "", default))).Code);
        images.Close("b"); Assert.Empty(Directory.GetFiles(root)); Directory.Delete(root);
    }

    [Fact]
    public async Task Confirmed_property_updates_cache_after_acknowledgment_and_failed_projection_returns_cache_stale()
    {
        await using var f = new NotionFixture(); var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        f.Database.Writer.Write("test.seed", s => cache.Replace(s, connection.Generation, [NotionCacheTests.Row(1) with { PageId = NotionFixture.Page, Status = "To Do" }], 10, false));
        QueueEditablePage(f); f.Enqueue("/v1/pages/" + NotionFixture.Page, "{\"id\":\"" + NotionFixture.Page + "\",\"last_edited_time\":\"r2\"}");
        var edits = new NotionEdits(f.Database, f.Settings, f.Http);
        Assert.Equal("confirmed", (await edits.PropertyAsync(new(NotionFixture.Source, NotionFixture.Page), "Status", "Done", "r1", default)).State);
        Assert.Equal("Done", Assert.Single(cache.Query(new(), default).Rows).Status);
        f.Database.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_projection BEFORE UPDATE ON notion_tasks BEGIN SELECT RAISE(ABORT,'injected projection failure'); END;"));
        QueueEditablePage(f); f.Enqueue("/v1/pages/" + NotionFixture.Page, "{\"id\":\"" + NotionFixture.Page + "\",\"last_edited_time\":\"r3\"}");
        Assert.Equal("cache_stale", (await edits.PropertyAsync(new(NotionFixture.Source, NotionFixture.Page), "Status", "Doing", "r1", default)).State);
        Assert.Equal("Done", Assert.Single(cache.Query(new(), default).Rows).Status);
        Assert.Equal(2, f.Handler.Requests.Count(r => r.Method == "PATCH"));
    }

    [Fact]
    public async Task Partial_two_block_acknowledgment_is_uncertain_and_preserves_literal_text()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueueEditablePage(f); f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children", "{\"results\":[{\"id\":\"" + NotionFixture.Other + "\"}]}");
        var receipt = await new NotionEdits(f.Database, f.Settings, f.Http).AppendTextAsync(new(NotionFixture.Source, NotionFixture.Page), "line \"one\"\nline two", default);
        Assert.Equal("uncertain", receipt.State);
        var request = System.Text.Json.JsonDocument.Parse(f.Handler.Requests.Single(r => r.Method == "PATCH").Body).RootElement;
        Assert.Equal("line \"one\"", request.GetProperty("children")[0].GetProperty("paragraph").GetProperty("rich_text")[0].GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Valid_image_upload_streams_to_the_fixed_endpoint_before_appending_one_block()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var root = Path.Combine(Path.GetTempPath(), "notion-image-test-" + Guid.NewGuid());
        using var images = new NotionImageUpload(root, f.Http, f.Settings, new NotionEdits(f.Database, f.Settings, f.Http));
        var bytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var lease = await images.BeginAsync("a", new(NotionFixture.Source, NotionFixture.Page), "image/png", bytes.Length, default);
        await images.ChunkAsync("a", lease.UploadId, 0, Convert.ToBase64String(bytes), default);
        QueueEditablePage(f);
        f.Enqueue("/v1/file_uploads", "{\"id\":\"" + NotionFixture.Other + "\"}");
        f.Enqueue("/v1/file_uploads/" + NotionFixture.Other + "/send", "{\"status\":\"uploaded\"}");
        f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children", "{\"results\":[{\"id\":\"" + NotionFixture.Other + "\"}]}");
        Assert.Equal("confirmed", (await images.CommitAsync("a", lease.UploadId, default)).State);
        Assert.Empty(Directory.GetFiles(root)); Directory.Delete(root);
        var body = System.Text.Json.JsonDocument.Parse(f.Handler.Requests[^1].Body).RootElement;
        Assert.Equal(NotionFixture.Other, body.GetProperty("children")[0].GetProperty("image").GetProperty("file_upload").GetProperty("id").GetString());
    }
    internal static void QueueEditablePage(NotionFixture f) => f.Enqueue("/v1/pages/" + NotionFixture.Page,
        "{\"id\":\"" + NotionFixture.Page + "\",\"parent\":{\"data_source_id\":\"" + NotionFixture.Source + "\"},\"last_edited_time\":\"r1\",\"properties\":{\"Status\":{\"type\":\"status\",\"status\":{\"name\":\"To Do\"}}}}");
}
