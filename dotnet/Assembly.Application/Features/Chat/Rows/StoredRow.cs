using System.Text.Json;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A row as saved and as sent. <c>FirstSeq</c> is set on page rows only: the seq of the item's first row,
/// which is where the item sits in the conversation. <c>PrevSeq</c> is set on live and catch-up rows: the seq
/// the reader should hold before this row. Finished messages' deltas are deleted, so it isn't always <c>Seq - 1</c>.
/// </summary>
public sealed record StoredRow(
    string SessionId,
    long Seq,
    string? RunId,
    string? ItemId,
    string Kind,
    JsonElement Payload,
    long CreatedAt,
    long? FirstSeq = null,
    long? PrevSeq = null);
