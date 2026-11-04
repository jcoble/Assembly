using Assembly.Application.Features.Notion;
using Assembly.Infrastructure.Notion;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionExportTests
{
    [Fact]
    public async Task Export_follows_nested_pages_and_freezes_values()
    {
        await using var f = new NotionFixture();
        await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueuePage(f); QueueBody(f, "first", true, "next"); QueueBody(f, "last");
        f.Enqueue("/v1/comments?block_id=" + NotionFixture.Page + "&page_size=100", "{}", 403);
        QueuePage(f);
        var x = await new NotionTaskExports(f.Settings, new NotionDetailReader(f.Http)).ExportAsync(new(NotionFixture.Source, NotionFixture.Page), default);
        Assert.Equal("notion", x.Source.Provider);
        Assert.Equal("TSK-1234", x.Task.Key);
        Assert.Equal("Full title", x.Task.Title);
        Assert.Contains("first", x.Task.Body); Assert.Contains("last", x.Task.Body);
        Assert.Equal("https://www.notion.so/" + NotionFixture.Page.Replace("-", ""), x.Task.Url);
        Assert.Contains(f.Handler.Requests, r => r.Path.EndsWith("start_cursor=next", StringComparison.Ordinal));
        QueuePage(f, "r2");
        Assert.Equal("r1", x.Revision);
        Assert.Equal("Full title", x.Task.Title);
    }
    [Fact]
    public async Task Failed_second_page_never_exports_title_as_body()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueuePage(f); QueueBody(f, "first", true, "next");
        f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100&start_cursor=next", "{}", 500);
        var error = await Assert.ThrowsAsync<NotionException>(() => new NotionTaskExports(f.Settings, new NotionDetailReader(f.Http)).ExportAsync(new(NotionFixture.Source, NotionFixture.Page), default));
        Assert.Equal("notion_body_incomplete", error.Code);
    }

    [Fact]
    public async Task Clear_during_http_read_rejects_the_old_export()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueuePage(f);
        var gate = f.Gate("/v1/pages/" + NotionFixture.Page);
        var export = new NotionTaskExports(f.Settings, new NotionDetailReader(f.Http)).ExportAsync(new(NotionFixture.Source, NotionFixture.Page), default);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Settings.ClearAsync(default);
        gate.Release.TrySetResult();
        Assert.Equal("notion_connection_changed", (await Assert.ThrowsAsync<NotionException>(() => export)).Code);
        Assert.False(f.Settings.Read().HasToken);
    }

    [Fact]
    public async Task Nested_children_and_a_complete_empty_body_are_exported_without_fallback()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        QueuePage(f);
        f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100", "{\"results\":[{\"id\":\"" + NotionFixture.Source + "\",\"type\":\"toggle\",\"has_children\":true,\"toggle\":{\"rich_text\":[{\"plain_text\":\"parent\"}]}}],\"has_more\":false}");
        f.Enqueue("/v1/blocks/" + NotionFixture.Source + "/children?page_size=100", "{\"results\":[{\"id\":\"" + NotionFixture.Other + "\",\"type\":\"paragraph\",\"has_children\":false,\"paragraph\":{\"rich_text\":[{\"plain_text\":\"nested last\"}]}}],\"has_more\":false}");
        f.Enqueue("/v1/comments?block_id=" + NotionFixture.Page + "&page_size=100", "{\"results\":[],\"has_more\":false}"); QueuePage(f);
        var producer = new NotionTaskExports(f.Settings, new NotionDetailReader(f.Http));
        Assert.Contains("  nested last", (await producer.ExportAsync(new(NotionFixture.Source, NotionFixture.Page), default)).Task.Body);
        QueuePage(f); f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100", "{\"results\":[],\"has_more\":false}");
        f.Enqueue("/v1/comments?block_id=" + NotionFixture.Page + "&page_size=100", "{\"results\":[],\"has_more\":false}"); QueuePage(f);
        Assert.Equal("", (await producer.ExportAsync(new(NotionFixture.Source, NotionFixture.Page), default)).Task.Body);
    }
    internal static void QueuePage(NotionFixture f, string revision = "r1") => f.Enqueue("/v1/pages/" + NotionFixture.Page,
        "{\"id\":\"" + NotionFixture.Page + "\",\"parent\":{\"data_source_id\":\"" + NotionFixture.Source + "\"},\"last_edited_time\":\"" + revision + "\",\"url\":\"https://www.notion.so/" + NotionFixture.Page.Replace("-", "") + "\",\"properties\":{\"Name\":{\"type\":\"title\",\"title\":[{\"plain_text\":\"Full title\"}]},\"ID\":{\"type\":\"unique_id\",\"unique_id\":{\"prefix\":\"TSK\",\"number\":1234}}}}");
    internal static void QueueBody(NotionFixture f, string text, bool more = false, string? cursor = null) => f.Enqueue(
        "/v1/blocks/" + NotionFixture.Page + "/children?page_size=100" + (text == "last" ? "&start_cursor=next" : ""),
        "{\"results\":[{\"id\":\"" + (text == "last" ? NotionFixture.Other : NotionFixture.Source) + "\",\"type\":\"paragraph\",\"has_children\":false,\"paragraph\":{\"rich_text\":[{\"plain_text\":\"" + text + "\"}]}}],\"has_more\":" + more.ToString().ToLowerInvariant() + ",\"next_cursor\":" + (cursor is null ? "null" : "\"" + cursor + "\"") + "}");
}
