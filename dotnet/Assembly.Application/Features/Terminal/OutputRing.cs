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

    /// <summary>The offset of the oldest byte kept.</summary>
    public long Start => throw new NotImplementedException();

    /// <summary>The offset just past the newest byte.</summary>
    public long End => throw new NotImplementedException();

    public void Append(ReadOnlySpan<byte> bytes) => throw new NotImplementedException();

    /// <summary>
    /// The bytes from <paramref name="offset"/>, raised to the oldest kept and to the last <see cref="Replay"/> bytes,
    /// at most <see cref="ReadLimit"/> of them.
    /// </summary>
    public OutputPiece Read(long offset) => throw new NotImplementedException();
}
