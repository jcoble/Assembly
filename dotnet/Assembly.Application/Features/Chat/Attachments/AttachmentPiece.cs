namespace Assembly.Application.Features.Chat.Attachments;

/// <summary>A bounded piece of the original image on its owning machine.</summary>
public sealed record AttachmentPiece(byte[] Data, long Size, string Mime);
