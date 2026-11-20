namespace Assembly.Application.Common.Acp.Messages;

/// <summary>One block of a prompt: text, which every agent must accept, or an image for an agent that advertises them.</summary>
public sealed record ContentBlock(string Type, string? Text = null, string? Data = null, string? MimeType = null);

/// <summary>A text block.</summary>
public static class TextBlock
{
    public static ContentBlock Of(string text) => new("text", text);
}

/// <summary>An image block: the image's bytes, base64, and its MIME type.</summary>
public static class ImageBlock
{
    public static ContentBlock Of(ReadOnlySpan<byte> bytes, string mimeType) => new("image", Data: Convert.ToBase64String(bytes), MimeType: mimeType);
}
