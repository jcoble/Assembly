namespace Assembly.Application.Features.Terminal;

/// <summary>
/// One row of the <c>terminal</c> topic: the output from <paramref name="PrevSeq"/> to <paramref name="Seq"/>, as byte
/// offsets. The last row is the exit: <paramref name="Seq"/> one past the end, no data, and its code.
/// </summary>
public sealed record OutputRow(long Seq, long PrevSeq, byte[]? Data = null, int? Exit = null);
