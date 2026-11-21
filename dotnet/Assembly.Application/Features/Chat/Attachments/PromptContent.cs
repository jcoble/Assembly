using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Features.Chat.Rows.Context;

namespace Assembly.Application.Features.Chat.Attachments;

/// <summary>
/// The blocks a prompt reaches the agent as. Its attached images go as image blocks when the agent advertises them, so
/// it never has to read a file to see one; each one sent drops its "Attached image:" line. An image that can't go
/// (another agent, not this session's, unreadable, or past the limit) keeps its line, and the agent reads the path.
/// </summary>
internal static class PromptContent
{
    /// <summary>The most image bytes one prompt carries, as the old app's remote prompt allowed.</summary>
    public const long MostImageBytes = 20 * 1024 * 1024;

    public static async Task<IReadOnlyList<ContentBlock>> BuildAsync(IPromptImages? images, string sessionId, string text,
        IReadOnlyList<string> attachmentIds, bool acceptsImages, CancellationToken cancellationToken)
    {
        if (images is null || !acceptsImages || attachmentIds.Count == 0) return [TextBlock.Of(text)];
        var (body, attached) = PromptPaths.Split(text);
        var lines = new List<string> { body };
        var blocks = new List<ContentBlock>();
        long total = 0;
        foreach (var (line, image) in attached)
        {
            var read = attachmentIds.Contains(image.Path)
                ? await images.ReadAsync(sessionId, image.Path, cancellationToken) : null;
            if (read is not null && total + read.Bytes.Length <= MostImageBytes)
            {
                total += read.Bytes.Length;
                blocks.Add(ImageBlock.Of(read.Bytes, read.MimeType));
            }
            else lines.Add(line);
        }
        if (blocks.Count == 0) return [TextBlock.Of(text)];
        var rest = string.Join('\n', lines.Where(part => part.Length > 0));
        return rest.Length > 0 ? [TextBlock.Of(rest), .. blocks] : blocks;
    }
}
