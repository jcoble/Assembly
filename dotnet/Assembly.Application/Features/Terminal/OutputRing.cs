namespace Assembly.Application.Features.Terminal;

/// <summary>
/// A terminal's kept output, as bytes (terminal#3, terminal#6). Offsets count every byte since the terminal started, so
/// they stay the same as the oldest bytes are dropped. Not thread safe: its terminal holds the lock.
/// </summary>
internal sealed class OutputRing
{
    public const int Cap = 16 * 1024 * 1024;
    public const int TrimTo = 12 * 1024 * 1024;

    /// <summary>The most a window coming back is shown (terminal#5).</summary>
    public const int Replay = 4 * 1024 * 1024;

    /// <summary>The most one read answers, as attachments' reads do (plan D4).</summary>
    public const int ReadLimit = 1024 * 1024;

    // Grows as output comes, up to Cap, so a quiet terminal holds little.
    private byte[] _buffer = new byte[64 * 1024];
    private int _count;

    /// <summary>The offset of the oldest byte kept.</summary>
    public long Start { get; private set; }

    /// <summary>The offset just past the newest byte.</summary>
    public long End => Start + _count;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > TrimTo)
        {
            Start += _count + bytes.Length - TrimTo;
            _count = 0;
            bytes = bytes[^TrimTo..];
        }
        if (_count + bytes.Length > Cap)
        {
            var drop = _count + bytes.Length - TrimTo;
            _buffer.AsSpan(drop, _count - drop).CopyTo(_buffer);
            _count -= drop;
            Start += drop;
        }
        if (_count + bytes.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Min(Cap, Math.Max(_buffer.Length * 2, _count + bytes.Length)));
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;
    }

    /// <summary>
    /// The bytes from <paramref name="offset"/>, raised to the oldest kept and to the last <see cref="Replay"/> bytes,
    /// at most <see cref="ReadLimit"/> of them.
    /// </summary>
    public OutputPiece Read(long offset)
    {
        var from = Math.Clamp(offset, Math.Max(Start, End - Replay), End);
        var length = (int)Math.Min(ReadLimit, End - from);
        return new OutputPiece(from, End, _buffer.AsSpan((int)(from - Start), length).ToArray());
    }
}
