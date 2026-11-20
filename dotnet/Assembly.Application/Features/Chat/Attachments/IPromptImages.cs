namespace Assembly.Application.Features.Chat.Attachments;

/// <summary>The images a prompt attaches, read on the machine that saved them and runs the session's agent.</summary>
public interface IPromptImages
{
    /// <summary>
    /// The image <paramref name="reference"/> names when it is one of the session's saved images, of an accepted type
    /// and within the size limit; else null, and the prompt keeps naming its path.
    /// </summary>
    Task<PromptImage?> ReadAsync(string sessionId, string reference, CancellationToken cancellationToken);
}

public sealed record PromptImage(byte[] Bytes, string MimeType);
