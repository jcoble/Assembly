using System.Buffers.Binary;
using System.IO.Compression;
using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Http;

using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;
namespace Assembly.Tests.Features.Diagnostics;

public sealed class ThumbnailTests
{
    [MacFact]
    public void An_image_with_alpha_becomes_a_png_at_most_360_px_on_its_long_edge()
    {
        var (bytes, mime) = Thumbnail.Make(Png(800, 400, alpha: true));

        Assert.Equal("image/png", mime);
        Assert.Equal((360, 180), PngSize(bytes));
    }

    [MacFact]
    public void An_opaque_image_becomes_a_jpeg()
    {
        var (bytes, mime) = Thumbnail.Make(Png(800, 400, alpha: false));

        Assert.Equal("image/jpeg", mime);
        Assert.Equal([0xFF, 0xD8, 0xFF], bytes[..3]);
    }

    [MacFact]
    public void An_image_too_wide_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Thumbnail.Make(Png(12_001, 1, alpha: false)));

        Assert.Equal("Image dimensions must be between 1x1 and 12000x12000", error.Message);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Thumbnail.Make([1, 2, 3, 4]));

        Assert.Equal("Only PNG, JPEG, GIF, and WebP images are supported", error.Message);
    }

    private static (int, int) PngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    // A plain grey PNG, 8-bit RGB or RGBA.
    private static byte[] Png(int width, int height, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var raw = new byte[height * (1 + width * channels)];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = i % (1 + width * channels) == 0 ? (byte)0 : (byte)128;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest))
            zlib.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(alpha ? 6 : 2);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
