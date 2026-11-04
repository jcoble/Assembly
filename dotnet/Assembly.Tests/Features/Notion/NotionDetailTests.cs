using Assembly.Application.Features.Notion;
using Assembly.Infrastructure.Notion;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionDetailTests
{
    [Fact]
    public async Task Changed_revision_rejects_mixed_body()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        NotionExportTests.QueuePage(f); NotionExportTests.QueueBody(f, "first");
        f.Enqueue("/v1/comments?block_id=" + NotionFixture.Page + "&page_size=100", "{\"results\":[],\"has_more\":false}");
        NotionExportTests.QueuePage(f, "r2");
        var error = await Assert.ThrowsAsync<NotionException>(() => new NotionDetailReader(f.Http).ReadAsync(new(NotionFixture.Source, NotionFixture.Page), default));
        Assert.Equal("notion_changed", error.Code);
    }
    [Fact]
    public async Task Cancelled_export_does_not_read_credentials_or_network()
    {
        await using var f = new NotionFixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NotionTaskExports(f.Settings, new NotionDetailReader(f.Http)).ExportAsync(new(NotionFixture.Source, NotionFixture.Page), stop.Token));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData("{\"results\":[],\"has_more\":true,\"next_cursor\":null}")]
    [InlineData("{\"results\":[],\"has_more\":true,\"next_cursor\":\"repeat\"}")]
    public async Task Incomplete_or_repeated_cursor_is_rejected(string body)
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        NotionExportTests.QueuePage(f);
        f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100", body);
        if (body.Contains("repeat", StringComparison.Ordinal))
            f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100&start_cursor=repeat", body);
        Assert.Equal("notion_body_incomplete", (await Assert.ThrowsAsync<NotionException>(() => new NotionDetailReader(f.Http).ReadAsync(new(NotionFixture.Source, NotionFixture.Page), default))).Code);
    }

    [Fact]
    public async Task Unsupported_body_remains_visible_readonly_but_cannot_export()
    {
        await using var f = new NotionFixture(); await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        NotionExportTests.QueuePage(f);
        f.Enqueue("/v1/blocks/" + NotionFixture.Page + "/children?page_size=100", "{\"results\":[{\"id\":\"" + NotionFixture.Other + "\",\"type\":\"unsupported\",\"has_children\":false,\"unsupported\":{}}],\"has_more\":false}");
        f.Enqueue("/v1/comments?block_id=" + NotionFixture.Page + "&page_size=100", "{}", 403);
        NotionExportTests.QueuePage(f);
        var detail = await new NotionDetailReader(f.Http).ReadAsync(new(NotionFixture.Source, NotionFixture.Page), default);
        Assert.True(detail.CommentsUnavailable);
        Assert.Equal("unsupported", Assert.Single(detail.Blocks).Kind);
        Assert.Equal("notion_unsupported_body", Assert.Throws<NotionException>(() => NotionBody.Markdown(detail.Blocks, detail.Url)).Code);
    }

    [Fact]
    public void Rich_body_conversion_keeps_code_table_and_stable_screenshot_identity()
    {
        static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        var blocks = new NotionBlock[] {
            new(NotionFixture.Source,"code",Json("{\"rich_text\":[{\"plain_text\":\"```\"}],\"language\":\"csharp\"}"),[]),
            new(NotionFixture.Other,"image",Json("{\"caption\":[{\"plain_text\":\"actual alt\"}],\"file\":{\"url\":\"https://signed.invalid/private?token=not-a-real-token\"}}"),[]),
            new(NotionFixture.Page,"table",Json("{\"has_column_header\":true}"),[
                new(NotionFixture.Source,"table_row",Json("{\"cells\":[[{\"plain_text\":\"A\"}],[{\"plain_text\":\"B\"}]]}"),[]),
                new(NotionFixture.Other,"table_row",Json("{\"cells\":[[{\"plain_text\":\"one\"}],[{\"plain_text\":\"two\"}]]}"),[])])
        };
        var body = NotionBody.Markdown(blocks, "https://www.notion.so/" + NotionFixture.Page.Replace("-", ""));
        Assert.Contains("````csharp\n```\n````", body);
        Assert.Contains("| A | B |\n| --- | --- |\n| one | two |", body);
        Assert.Contains("![actual alt](https://www.notion.so/" + NotionFixture.Page.Replace("-", "") + "#" + NotionFixture.Other.Replace("-", "") + ")", body);
        Assert.DoesNotContain("signed.invalid", body);
    }
}
