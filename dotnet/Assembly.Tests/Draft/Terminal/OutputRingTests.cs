using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

public sealed class OutputRingTests
{
    private const int Mb = 1024 * 1024;

    [Fact]
    public void Keeps_the_bytes_exactly_even_a_character_split_across_two_reads()
    {
        // Pins: terminal#3
        var ring = new OutputRing();
        ring.Append([0xe4, 0xb8]);
        ring.Append([0x96, 0xff]);

        var piece = ring.Read(0);

        Assert.Equal(0, piece.Offset);
        Assert.Equal(4, piece.End);
        Assert.Equal(new byte[] { 0xe4, 0xb8, 0x96, 0xff }, piece.Data);
    }

    [Fact]
    public void Past_16_MB_it_drops_the_oldest_down_to_12_MB_and_offsets_stay_the_same()
    {
        // Pins: terminal#6
        var ring = new OutputRing();
        long total = 0;
        while (total <= OutputRing.Cap)
            total += AppendPattern(ring, total, Mb);

        Assert.Equal(total, ring.End);
        Assert.True(ring.End - ring.Start <= OutputRing.TrimTo, $"kept {ring.End - ring.Start} bytes");
        Assert.True(ring.End - ring.Start > OutputRing.TrimTo - Mb, $"kept only {ring.End - ring.Start} bytes");
        var tail = ring.Read(ring.End - 10);
        Assert.Equal(ring.End - 10, tail.Offset);
        Assert.Equal(Pattern(ring.End - 10, 10), tail.Data);
    }

    [Fact]
    public void A_read_starts_no_earlier_than_the_last_4_MB()
    {
        // Pins: terminal#5
        var ring = new OutputRing();
        AppendPattern(ring, 0, 6 * Mb);

        var piece = ring.Read(0);

        Assert.Equal(ring.End - OutputRing.Replay, piece.Offset);
        Assert.Equal(Pattern(piece.Offset, piece.Data.Length), piece.Data);
    }

    [Fact]
    public void A_read_answers_at_most_1_MB_and_says_where_the_output_ends()
    {
        // Pins: terminal#5
        var ring = new OutputRing();
        AppendPattern(ring, 0, 2 * Mb + 5);

        var first = ring.Read(0);
        var second = ring.Read(first.Offset + first.Data.Length);

        Assert.Equal(OutputRing.ReadLimit, first.Data.Length);
        Assert.Equal(2 * Mb + 5, first.End);
        Assert.Equal(Pattern(first.Data.Length, second.Data.Length), second.Data);
        Assert.Empty(ring.Read(ring.End).Data);
    }

    private static int AppendPattern(OutputRing ring, long offset, int count)
    {
        ring.Append(Pattern(offset, count));
        return count;
    }

    // Each byte is its offset mod 251, so a byte in the wrong place shows.
    private static byte[] Pattern(long offset, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
            bytes[i] = (byte)((offset + i) % 251);
        return bytes;
    }
}
