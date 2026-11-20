using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows.Copies;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>The owner's reads another machine's saved copy downloads from (TSK-1692). Both are bulk calls.</summary>
internal sealed partial class RowsFeature
{
    /// <summary>The items the first chunk of a download carries, so the newest screen is saved first.</summary>
    public const int FirstChunkItems = 100;

    private void RegisterCopyReads(ICallRegistry calls)
    {
        calls.Read(Topic, "before", RowJsonContext.Rows.BeforeArgs, RowJsonContext.Rows.BeforeChunk, (args, call) =>
            Task.FromResult(_store.History.ItemsBefore(args.Session, args.Before, args.Items is { } items ? Math.Max(items, 1) : null,
                call.Cancellation)), bulk: true);
        calls.Read(Topic, "heads", RowJsonContext.Rows.HeadsArgs, RowJsonContext.Rows.HeadsReply, (args, call) =>
            Task.FromResult(new HeadsReply(_store.History.Facts(args.Sessions, call.Cancellation))), bulk: true);
    }

    /// <param name="Before">Items whose first row is below this seq; null reads from the top.</param>
    /// <param name="Items">The most items to send, or only the byte budget when null.</param>
    internal sealed record BeforeArgs([property: JsonRequired] string Session, long? Before = null, int? Items = null);

    internal sealed record HeadsArgs([property: JsonRequired] IReadOnlyList<string> Sessions);

    internal sealed record HeadsReply(IReadOnlyDictionary<string, HistoryFacts> Sessions);
}
