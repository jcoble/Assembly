using System.Text.Json;

namespace Assembly.Application.Features.Notion;

public sealed record NotionTaskRef(string DataSourceId, string PageId);
public sealed record NotionSource(string Provider, string DataSourceId, string PageId);
public sealed record TaskSnapshot(string Key, string Title, string Body, string Url);
public sealed record NotionTaskExport(NotionSource Source, string Revision, TaskSnapshot Task);
public sealed record NotionBlock(string Id, string Kind, JsonElement Content, IReadOnlyList<NotionBlock> Children);
public sealed record NotionDetail(string Revision, JsonElement Properties, IReadOnlyList<NotionBlock> Blocks,
    IReadOnlyList<JsonElement> Comments, bool CommentsUnavailable, string Url);
public sealed record NotionDetailArgs(NotionTaskRef Source, string? After = null);
public sealed record DetailPage(string OperationId, string Revision, JsonElement? Properties, IReadOnlyList<NotionBlock> Blocks,
    IReadOnlyList<JsonElement> Comments, string? Next, bool BodyComplete, bool CommentsUnavailable);
public interface INotionDetailReader
{
    Task<NotionDetail> ReadAsync(NotionTaskRef source, CancellationToken ct);
}

/// <summary>A single completed read per connection, dropped on replacement, cancellation or close.</summary>
public sealed class NotionDetailPages(NotionSettings settings, INotionDetailReader reader)
{
    private sealed record Read(string Id, NotionTaskRef Source, long Generation, NotionDetail Detail, int Offset);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Read> _reads = new();
    public void Close(string connection) => _reads.TryRemove(connection, out _);
    public async Task<DetailPage> PageAsync(string connection, NotionDetailArgs args, CancellationToken ct)
    {
        Read read;
        if (args.After is null)
        {
            Close(connection);
            var current = settings.Read();
            var source = new NotionTaskRef(NotionIds.Canonical(args.Source.DataSourceId), NotionIds.Canonical(args.Source.PageId));
            if (!current.HasToken || source.DataSourceId != current.DataSourceId) throw new NotionException("notion_source_mismatch");
            var detail = await reader.ReadAsync(source, ct);
            settings.Check(current.Generation);
            read = new(Guid.NewGuid().ToString("D"), source, current.Generation, detail, 0);
        }
        else
        {
            if (!_reads.TryRemove(connection, out read!) || args.After != read.Id + ":" + read.Offset || args.Source != read.Source) throw new NotionException("notion_detail_expired");
        }
        ct.ThrowIfCancellationRequested(); settings.Check(read.Generation);
        var blocks = new List<NotionBlock>();
        var index = read.Offset;
        while (index < read.Detail.Blocks.Count && blocks.Count < 100)
        {
            blocks.Add(read.Detail.Blocks[index]);
            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(blocks, NotionJson.Default.ListNotionBlock).Length > 700 * 1024)
            {
                blocks.RemoveAt(blocks.Count - 1);
                if (blocks.Count == 0) throw new NotionException("notion_too_large");
                break;
            }
            index++;
        }
        var next = index < read.Detail.Blocks.Count ? read.Id + ":" + index : null;
        var page = new DetailPage(read.Id, read.Detail.Revision, read.Offset == 0 ? read.Detail.Properties : null, blocks,
            next is null ? read.Detail.Comments : [], next, next is null, read.Detail.CommentsUnavailable);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, NotionJson.Default.DetailPage).Length > 768 * 1024 - 1024) throw new NotionException("notion_too_large");
        if (next is not null) _reads[connection] = read with { Offset = index };
        return page;
    }
}
