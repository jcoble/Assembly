namespace Assembly.Application.Features.Notion;

public sealed record NotionQuery(int Offset = 0, int Limit = 25, string? Search = null, string? ProjectId = null,
    string[]? Statuses = null, string[]? Priorities = null, string Sort = "number", string Direction = "desc")
{
    public void Validate()
    {
        if (Offset < 0 || Limit is < 1 or > 100 || Sort is not ("number" or "title") || Direction is not ("asc" or "desc") ||
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, NotionJson.Default.NotionQuery).Length > 256 * 1024 - 1024 ||
            Search?.Length > 1024 || Statuses?.Length > 100 || Priorities?.Length > 100) throw new NotionException("invalid_notion_query");
    }
}
public sealed record NotionTaskRow(string SourceId, string PageId, string Title, long KeyNumber, string TaskKey,
    string ProjectId, string ProjectTitle, string Status, string Priority, string Assignee, string? Due, string Url, long FetchedMs);
public sealed record NotionProjectOption(string Id, string Title);
public sealed record NotionTaskPage(IReadOnlyList<NotionTaskRow> Rows, bool HasMore, IReadOnlyList<string> Statuses, IReadOnlyList<string> Priorities, IReadOnlyList<NotionProjectOption> Projects,
    long FetchedMs, bool Truncated, long Generation, long Revision);
public sealed record NotionRefreshReceipt(int Count, long FetchedMs, bool Truncated, long Generation);
public sealed record NotionRefreshArgs(long ExpectedGeneration);
