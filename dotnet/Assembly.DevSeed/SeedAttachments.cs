using System.IO.Compression;
using System.Text;

namespace Assembly.DevSeed;

/// <summary>Small real PNGs for the chats that attach one, drawn here so nothing binary is checked in.</summary>
public static class SeedAttachments
{
    public static void Write(SeedWorld world, IReadOnlyList<SeedChat> sessions)
    {
        foreach (var chat in sessions)
            foreach (var (file, seed) in chat.Attachments)
            {
                var path = world.Attachment(chat.Id, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Picture(seed));
            }
    }

    // A fake app screenshot: a dark window with a sidebar and some bars, varied by the seed.
    private static byte[] Picture(int seed)
    {
        const int width = 320, height = 200;
        var pixels = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            for (var x = 0; x < width; x++)
            {
                var bar = x > 90 && x < 300 - (y / 20 * 37 + seed * 23) % 120 && y % 20 is > 6 and < 14;
                var side = x < 80;
                var color = y < 18 ? (40, 44, 52) : side ? (30, 33, 40) : bar ? (90 + seed * 30 % 120, 140, 210) : (22, 24, 30);
                pixels[row + 1 + x * 3] = (byte)color.Item1; pixels[row + 2 + x * 3] = (byte)color.Item2; pixels[row + 3 + x * 3] = (byte)color.Item3;
            }
        }
        using var image = new MemoryStream();
        image.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        Big(header, 0, width); Big(header, 4, height); header[8] = 8; header[9] = 2;
        Chunk(image, "IHDR", header);
        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(pixels);
            Chunk(image, "IDAT", packed.ToArray());
        }
        Chunk(image, "IEND", []);
        return image.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] data)
    {
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(kind).CopyTo(body, 0);
        data.CopyTo(body, 4);
        var number = new byte[4];
        Big(number, 0, (uint)data.Length); output.Write(number);
        output.Write(body);
        Big(number, 0, Crc(body)); output.Write(number);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static void Big(byte[] into, int at, uint value)
    {
        into[at] = (byte)(value >> 24); into[at + 1] = (byte)(value >> 16); into[at + 2] = (byte)(value >> 8); into[at + 3] = (byte)value;
    }
}
