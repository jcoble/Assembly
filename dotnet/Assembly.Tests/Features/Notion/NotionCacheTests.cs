using Assembly.Application.Features.Notion;
using Assembly.Infrastructure.Notion;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionCacheTests
{
    [Fact]
    public async Task Failed_refresh_retains_last_successful_snapshot()
    {
        await using var f = new NotionFixture();
        var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        f.Database.Writer.Write("test.seed", s => cache.Replace(s, connection.Generation, [Row(1)], 10, false));
        f.Enqueue("/v1/data_sources/" + NotionFixture.Source + "/query", "{}", 500);
        await Assert.ThrowsAsync<NotionException>(() => new NotionRefresh(f.Http, cache, f.Settings, TimeProvider.System).RefreshAsync(connection.Generation, default));
        Assert.Equal("Title 1", Assert.Single(cache.Query(new(), default).Rows).Title);
    }
    [Fact]
    public async Task Sql_filters_sort_and_paging_have_exact_reachable_rows()
    {
        await using var f = new NotionFixture();
        var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        var rows = Enumerable.Range(1, 60).Select(i => Row(i) with { Status = i <= 30 ? "Doing" : "Done" }).ToArray();
        f.Database.Writer.Write("test.seed", s => cache.Replace(s, connection.Generation, rows, 10, false));
        var page = cache.Query(new(Statuses: ["Doing"], Priorities: ["High"], ProjectId: "project-a"), default);
        Assert.Equal(25, page.Rows.Count); Assert.True(page.HasMore);
        Assert.Equal(1, page.Rows[0].KeyNumber);
        Assert.Equal(5, cache.Query(new(Offset: 25, Statuses: ["Doing"]), default).Rows.Count);
        Assert.Empty(cache.Query(new(Search: "%"), default).Rows);
        Assert.Throws<NotionException>(() => cache.Query(new(Limit: 101), default));
    }
    [Fact]
    public async Task Cleared_generation_cannot_recreate_cache()
    {
        await using var f = new NotionFixture();
        var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        await f.Settings.ClearAsync(default);
        Assert.Throws<NotionException>(() => f.Database.Writer.Write("test.old", s => cache.Replace(s, connection.Generation, [Row(1)], 10, false)));
        Assert.Empty(cache.Query(new(), default).Rows);
    }

    [Theory]
    [InlineData(100, 5)]
    [InlineData(60, 9)]
    public async Task Five_hundred_row_cap_preserves_a_visible_truncation_receipt(int pageSize, int pageCount)
    {
        await using var f = new NotionFixture(); var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        for (var page = 0; page < pageCount; page++)
        {
            var results = Enumerable.Range(page * pageSize + 1, pageSize).Select(n => new
            {
                id = Guid.NewGuid().ToString("D"),
                parent = new { data_source_id = NotionFixture.Source },
                url = "https://www.notion.so/" + NotionFixture.Page.Replace("-", ""),
                properties = new { Name = new { type = "title", title = new[] { new { plain_text = "Title " + n } } } }
            }).ToArray();
            // URL must identify the actual page; build each result with its own canonical URL.
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { results, has_more = true, next_cursor = "cursor-" + page }))!;
            foreach (var result in json["results"]!.AsArray()) result!["url"] = "https://www.notion.so/" + result["id"]!.GetValue<string>().Replace("-", "");
            f.Enqueue("/v1/data_sources/" + NotionFixture.Source + "/query", json.ToJsonString());
        }
        var receipt = await new NotionRefresh(f.Http, cache, f.Settings, TimeProvider.System).RefreshAsync(connection.Generation, default);
        Assert.Equal(500, receipt.Count); Assert.True(receipt.Truncated);
        var first = cache.Query(new(Limit: 100), default);
        Assert.Equal(100, first.Rows.Count); Assert.True(first.Truncated); Assert.True(first.HasMore);
        Assert.Equal(pageCount, f.Handler.Requests.Count);
        Assert.All(f.Handler.Requests, r => Assert.Equal(100, System.Text.Json.JsonDocument.Parse(r.Body).RootElement.GetProperty("page_size").GetInt32()));
    }

    [Fact]
    public async Task Replacement_insert_failure_rolls_back_the_delete_and_metadata()
    {
        await using var f = new NotionFixture(); var connection = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        var cache = new NotionCache(f.Database, f.Settings);
        f.Database.Writer.Write("test.seed", s => cache.Replace(s, connection.Generation, [Row(1)], 10, false));
        f.Database.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_cache BEFORE INSERT ON notion_tasks BEGIN SELECT RAISE(ABORT,'injected cache failure'); END;"));
        Assert.ThrowsAny<Exception>(() => f.Database.Writer.Write("test.replace", s => cache.Replace(s, connection.Generation, [Row(2)], 20, true)));
        var page = cache.Query(new(), default);
        Assert.Equal("Title 1", Assert.Single(page.Rows).Title); Assert.Equal(10, page.FetchedMs); Assert.False(page.Truncated); Assert.Equal(1, page.Revision);
    }

    [Fact]
    public async Task Saved_view_roundtrips_only_known_filters_with_array_defaults()
    {
        await using var f = new NotionFixture();
        var saved = await f.RequestAsync("saveView", new { search = "specific task", statuses = new[] { "Doing" }, sort = "title", direction = "desc" });
        var view = await f.RequestAsync("view", new { });
        Assert.Equal(saved.GetRawText(), view.GetRawText());
        Assert.Equal("specific task", view.GetProperty("search").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, view.GetProperty("priorities").ValueKind);
        Assert.Equal("Doing", view.GetProperty("statuses")[0].GetString());
    }
    internal static NotionTaskRow Row(int number) => new(NotionFixture.Source, Guid.NewGuid().ToString("D"), "Title " + number,
        number, "TSK-" + number, "project-a", "Project A", "Doing", "High", "", null, "https://www.notion.so/" + NotionFixture.Page.Replace("-", ""), 10);
}
