namespace Assembly.Engine.Attachments;

/// <summary>An image type we accept, its file extension, and the bytes its files start with.</summary>
public sealed record ImageType(string ContentType, string Extension, byte[] Magic)
{
    // Assembly's image limits (attachments.rs there): these four types, checked by their first bytes.
    public static readonly IReadOnlyList<ImageType> All =
    [
        new("image/png", "png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]),
        new("image/jpeg", "jpg", [0xFF, 0xD8, 0xFF]),
        new("image/gif", "gif", "GIF8"u8.ToArray()),
        new("image/webp", "webp", "RIFF"u8.ToArray()),
    ];

    public static ImageType? ForContentType(string? contentType) =>
        All.FirstOrDefault(type => type.ContentType == contentType?.Split(';')[0].Trim());

    public static ImageType? ForExtension(string extension) => All.FirstOrDefault(type => type.Extension == extension);

    /// <summary>Whether the bytes really are this type. A WebP file is RIFF with <c>WEBP</c> at byte 8.</summary>
    public bool Matches(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Magic) && (Extension != "webp" || (bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8)));
}
