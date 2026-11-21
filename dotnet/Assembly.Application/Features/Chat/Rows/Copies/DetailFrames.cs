using System.Text;
using System.Text.Json;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>What fitting a copy read's frame needs of one row: its item, its kind and its JSON size.</summary>
internal readonly record struct FrameRow(string? Item, string Kind, long Bytes);

/// <summary>
/// One copy read's frame (TSK-1692): rows, each tool item with its detail, under the 4 MB one read may send. Each run of
/// one item's rows stays whole. The first always comes, its detail cut smaller until it fits; the rest come while they fit.
/// </summary>
internal static class DetailFrames
{
    /// <summary>The most row and detail JSON one read sends, leaving room under 4 MB for its envelope.</summary>
    public const long Budget = 4 * 1024 * 1024 - 64 * 1024;

    // The smallest cut a detail that fills a frame alone is read at.
    private const int SmallestCap = 4 * 1024;

    /// <summary>
    /// How many of the rows fit in <paramref name="budget"/>, and the details of their tool items when
    /// <paramref name="detail"/> reads them (item, cap). Each tool item's detail comes once, with its first row. With
    /// <paramref name="wholeItems"/>, a run of one item's rows comes whole or not at all.
    /// </summary>
    public static (int Rows, IReadOnlyList<SavedToolDetail> Details) Fit(IReadOnlyList<FrameRow> rows,
        Func<string, int, SavedToolDetail?>? detail, bool wholeItems, long budget = Budget)
    {
        var details = new List<SavedToolDetail>();
        var seen = new HashSet<string>();
        var kept = 0;
        var used = 0L;
        while (kept < rows.Count)
        {
            // An item's run of rows, or one row.
            var end = kept + 1;
            while (wholeItems && end < rows.Count && rows[kept].Item is { } item && rows[end].Item == item) end++;
            var size = 0L;
            var tool = false;
            for (var at = kept; at < end; at++)
            {
                size += rows[at].Bytes + 1;
                tool |= rows[at].Kind == RowKind.Tool;
            }
            // Its tool detail, read once per item.
            SavedToolDetail? found = null;
            var weight = 0L;
            if (detail is not null && tool && rows[kept].Item is { } id && !seen.Contains(id))
                (found, weight) = Read(detail, id, budget - size, shrink: kept == 0);
            if (kept > 0 && used + size + weight > budget) break;
            if (found is not null)
            {
                details.Add(found);
                seen.Add(found.Item);
            }
            used += size + weight;
            kept = end;
        }
        return (kept, details);
    }

    /// <summary>The JSON size one detail adds to a frame.</summary>
    public static long Bytes(SavedToolDetail detail) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(detail, RowJsonContext.Rows.SavedToolDetail)) + 1;

    // The item's detail at the full cap, or, for a frame's first item, at the largest cap that fits its room.
    private static (SavedToolDetail? Detail, long Bytes) Read(Func<string, int, SavedToolDetail?> detail, string item, long room, bool shrink)
    {
        for (var cap = RowsFeature.ToolCap; ; cap /= 2)
        {
            if (detail(item, cap) is not { } found) return (null, 0);
            var bytes = Bytes(found);
            if (bytes <= room || !shrink || cap <= SmallestCap) return (found, bytes);
        }
    }
}
