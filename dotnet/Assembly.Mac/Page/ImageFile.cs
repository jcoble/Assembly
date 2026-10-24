namespace Assembly.Mac.Page;

/// <summary>An image file the page attaches by its path: a regular PNG, JPEG, GIF or WebP file of at most 20 MB.</summary>
internal static class ImageFile
{
    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>Its bytes and type, checked before and after reading.</summary>
    public static (byte[] Bytes, string Mime) Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException(Directory.Exists(path) ? "Only files can be attached" : $"{path} doesn't exist");
        if (info.Length is 0 or > MaxBytes)
            throw new InvalidOperationException("Image must be between 1 byte and 20 MB");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > (int)MaxBytes)
            throw new InvalidOperationException("Image must be between 1 byte and 20 MB");
        return (bytes, MimeOf(bytes) ?? throw new InvalidOperationException("Only PNG, JPEG, GIF, and WebP images are supported"));
    }

    // From the file's first bytes, not its name.
    internal static string? MimeOf(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };
}
