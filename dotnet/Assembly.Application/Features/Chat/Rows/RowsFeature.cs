using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A session's rows over the socket (plan 5.1, 5.7, C1): the <c>rows</c> topic, keyed by session, with <c>after</c> for a
/// resume, and <c>rows/page</c> for the history above it. A resume catches up at most a page; one further behind, or a
/// first open, starts from the session's last seq, and the page call loads the history before it.
/// </summary>
internal sealed class RowsFeature : IFeature
{
    public const string Topic = "rows";

    /// <summary>The most rows a page call reads, and the most a resume catches up.</summary>
    public const int PageLimit = 500;

    private readonly LiveHub _hub;
    private readonly RowStore _store;

    public RowsFeature(LiveHub hub, RowStore store)
    {
        _hub = hub;
        _store = store;
    }

    public void Register(ICallRegistry calls)
    {
        // Under the Topics lock, so a row saved meanwhile is in the catch-up or published after it (5.7). It reads the
        // store, never the hub, whose lock is taken before the Topics one when it publishes.
        calls.Rows(Topic, (session, after, _) =>
        {
            var last = _store.LastSeq(session);
            if (after is >= 0 and var from && from <= last && _store.RowsAfter(session, from, PageLimit + 1) is { Count: <= PageLimit } rows)
                return (from, rows.Select(RowWire.ToJson).ToList());
            return (last, []);
        });
        calls.Read(Topic, "page", RowJsonContext.Rows.PageArgs, RowJsonContext.Rows.JsonElement, (args, call) =>
            Task.FromResult(PageJson(_hub.Page(args.Session, args.Before, Math.Clamp(args.Limit, 1, PageLimit), call.Cancellation))));
    }

    // {rows, hasBefore, before}, as GET /sessions/{id}/rows answers it.
    private static JsonElement PageJson(RowPage page)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, RowWire.Options))
        {
            json.WriteStartObject();
            json.WriteStartArray("rows");
            foreach (var row in page.Rows)
                RowWire.Write(json, row);
            json.WriteEndArray();
            json.WriteBoolean("hasBefore", page.HasBefore);
            if (page.Before is { } before)
                json.WriteNumber("before", before);
            else
                json.WriteNull("before");
            json.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary><c>rows/page</c>'s args: the newest items that start below <c>before</c>.</summary>
    internal sealed record PageArgs([property: JsonRequired] string Session, long Before = long.MaxValue, int Limit = 50);
}
