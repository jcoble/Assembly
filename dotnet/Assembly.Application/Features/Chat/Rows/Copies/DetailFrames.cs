using System.Text;
using System.Text.Json;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows.Copies;

/// <summary>What fitting a copy read's frame needs of one row: its item, its kind and its JSON size.</summary>
internal readonly record struct FrameRow(string? Item, string Kind, long Bytes);

/// <summary>
/// The detail reads a frame is fitted with, each one query for many items: their sizes with each field counted to a cap,
/// and their details cut to a cap.
/// </summary>
internal sealed record DetailReads(Func<IReadOnlyList<string>, int, IReadOnlyDictionary<string, long>> Sizes,
    Func<IReadOnlyList<string>, int, IReadOnlyList<SavedToolDetail>> Read);

/// <summary>
/// One copy read's frame (TSK-1692): rows, each tool item with its detail, under the 4 MB one read may send. The first
/// row, or with whole items the first item's rows, always comes, its detail cut smaller until it fits; the rest come
/// while they fit. The details are sized in one query and read in one more.
/// </summary>
internal static class DetailFrames
{
    /// <summary>The most row and detail JSON one read sends, leaving room under 4 MB for its envelope.</summary>
    public const long Budget = 4 * 1024 * 1024 - 64 * 1024;

    // The smallest cut a detail that fills a frame alone is read at.
    private const int SmallestCap = 4 * 1024;

    // A run of rows that comes whole, and the tool item whose detail comes with it, if any.
    private sealed record Unit(int End, long Bytes, string? Tool);

    /// <summary>
    /// How many of the rows fit in <paramref name="budget"/>, and the details of their tool items when
    /// <paramref name="details"/> is given. Each tool item's detail comes once, with its first row. With
    /// <paramref name="wholeItems"/>, a run of one item's rows comes whole or not at all.
    /// </summary>
    public static (int Rows, IReadOnlyList<SavedToolDetail> Details) Fit(IReadOnlyList<FrameRow> rows, DetailReads? details,
        bool wholeItems, long budget = Budget)
    {
        var units = Units(rows, wholeItems, details is not null);
        var tools = units.Where(unit => unit.Tool is not null).Select(unit => unit.Tool!).ToList();
        var sizes = details is null || tools.Count == 0 ? new Dictionary<string, long>() : details.Sizes(tools, RowsFeature.ToolCap);
        // Fit by the sizes the fields will have, then read only the details that fit.
        var kept = new List<Unit>();
        var used = 0L;
        foreach (var unit in units)
        {
            var weight = unit.Bytes + (unit.Tool is { } tool ? sizes.GetValueOrDefault(tool) : 0);
            if (kept.Count > 0 && used + weight > budget) break;
            kept.Add(unit);
            used += weight;
        }
        if (details is null || kept.Count == 0) return (kept.Count == 0 ? 0 : kept[^1].End, []);
        var read = Read(details, kept, RowsFeature.ToolCap);
        // JSON escaping can make a detail larger than its size: drop whole runs from the end, then cut the first's detail.
        while (Total(kept, read) > budget && kept.Count > 1)
        {
            kept.RemoveAt(kept.Count - 1);
            read = [.. read.Where(found => kept.Any(unit => unit.Tool == found.Item))];
        }
        for (var cap = RowsFeature.ToolCap / 2; Total(kept, read) > budget && cap >= SmallestCap; cap /= 2)
            read = Read(details, kept, cap);
        return (kept[^1].End, read);
    }

    /// <summary>The JSON size one detail adds to a frame.</summary>
    public static long Bytes(SavedToolDetail detail) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(detail, RowJsonContext.Rows.SavedToolDetail)) + 1;

    private static IReadOnlyList<SavedToolDetail> Read(DetailReads details, IReadOnlyList<Unit> kept, int cap)
    {
        var tools = kept.Where(unit => unit.Tool is not null).Select(unit => unit.Tool!).ToList();
        return tools.Count == 0 ? [] : details.Read(tools, cap);
    }

    private static long Total(IReadOnlyList<Unit> kept, IReadOnlyList<SavedToolDetail> read) =>
        kept.Sum(unit => unit.Bytes) + read.Sum(Bytes);

    // Each run of rows that comes whole, with the tool item it brings first.
    private static List<Unit> Units(IReadOnlyList<FrameRow> rows, bool wholeItems, bool withDetails)
    {
        var units = new List<Unit>();
        var seen = new HashSet<string>();
        for (var start = 0; start < rows.Count;)
        {
            var end = start + 1;
            while (wholeItems && end < rows.Count && rows[start].Item is { } item && rows[end].Item == item) end++;
            var bytes = 0L;
            var tool = false;
            for (var at = start; at < end; at++)
            {
                bytes += rows[at].Bytes + 1;
                tool |= rows[at].Kind == RowKind.Tool;
            }
            var brings = withDetails && tool && rows[start].Item is { } id && seen.Add(id) ? id : null;
            units.Add(new Unit(end, bytes, brings));
            start = end;
        }
        return units;
    }
}
