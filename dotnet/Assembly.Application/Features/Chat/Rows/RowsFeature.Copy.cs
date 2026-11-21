using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// The owner's reads another machine's saved copy downloads from (TSK-1692). All are bulk calls. Tool details travel with
/// the rows here, never on the rows topic.
/// </summary>
internal sealed partial class RowsFeature
{
    /// <summary>The items the first chunk of a download carries, so the newest screen is saved first.</summary>
    public const int FirstChunkItems = 100;

    private void RegisterCopyReads(ICallRegistry calls)
    {
        calls.Read(Topic, "before", RowJsonContext.Rows.BeforeArgs, RowJsonContext.Rows.BeforeChunk, (args, call) =>
            Task.FromResult(Before(args, call.Cancellation)), bulk: true);
        calls.Read(Topic, "heads", RowJsonContext.Rows.HeadsArgs, RowJsonContext.Rows.HeadsReply, (args, call) =>
            Task.FromResult(new HeadsReply(_store.History.Facts(args.Sessions, call.Cancellation))), bulk: true);
        // A finished run's user rows, whose acknowledgment a copy saved before it was written doesn't have, and its tools'
        // details, which a copy that followed the run live doesn't have.
        calls.Read(Topic, "turn", RowJsonContext.Rows.TurnArgs, RowJsonContext.Rows.TurnRows, (args, call) =>
            Task.FromResult(Turn(args, call.Cancellation)), bulk: true);
    }

    // Whole items, newest first, with their tool details, as many as one frame holds.
    private BeforeChunk Before(BeforeArgs args, CancellationToken ct)
    {
        var chunk = _store.History.ItemsBefore(args.Session, args.Before, args.Items is { } items ? Math.Max(items, 1) : null, ct);
        var (kept, details) = DetailFrames.Fit([.. chunk.Rows.Select(Frame)], Detail(args.Session, ct), wholeItems: true);
        return chunk with { Rows = [.. chunk.Rows.Take(kept)], Details = details };
    }

    // The run's user rows and the details of its tools from After on, as many as one frame holds; Next says where to read on.
    private TurnRows Turn(TurnArgs args, CancellationToken ct)
    {
        var users = _store.History.UserRows(args.Session, args.Run, ct);
        var tools = _store.History.ToolItems(args.Session, args.Run, args.After ?? 0, ct);
        var budget = DetailFrames.Budget - users.Sum(user => Encoding.UTF8.GetByteCount(user.GetRawText()) + 1L);
        var (kept, details) = DetailFrames.Fit([.. tools.Select(tool => new FrameRow(tool.Item, RowKind.Tool, 0))],
            Detail(args.Session, ct), wholeItems: false, budget);
        return new TurnRows(users, details, kept < tools.Count ? tools[kept - 1].Seq : null);
    }

    private Func<string, int, SavedToolDetail?> Detail(string session, CancellationToken ct) =>
        (item, cap) => _store.DetailToSave(session, item, cap, ct);

    private static FrameRow Frame(JsonElement row) =>
        new(row.GetProperty("itemId").GetString(), row.GetProperty("kind").GetString()!, Encoding.UTF8.GetByteCount(row.GetRawText()));

    /// <param name="Before">Items whose first row is below this seq; null reads from the top.</param>
    /// <param name="Items">The most items to send, or only the byte budget when null.</param>
    internal sealed record BeforeArgs([property: JsonRequired] string Session, long? Before = null, int? Items = null);

    internal sealed record HeadsArgs([property: JsonRequired] IReadOnlyList<string> Sessions);

    internal sealed record HeadsReply(IReadOnlyDictionary<string, HistoryFacts> Sessions);

    /// <param name="After">Reads the details of the run's tools whose last row is past this seq.</param>
    internal sealed record TurnArgs([property: JsonRequired] string Session, [property: JsonRequired] string Run, long? After = null);

    /// <summary>
    /// The run's user rows, as the owner holds them now, and its tools' details. <paramref name="Next"/> is the
    /// <c>After</c> to read the rest of the details from, when one frame didn't hold them all.
    /// </summary>
    internal sealed record TurnRows(IReadOnlyList<JsonElement> Users, IReadOnlyList<SavedToolDetail>? Details = null, long? Next = null);
}
