using System.Text.Json;

namespace Assembly.Engine.Messages;

public sealed record OwnerPromptJournalEnvelope(string Kind, int Version, string RequestSha256,
    long TextUtf8Bytes, long AttachmentJsonUtf8Bytes, long AttachmentCount);

/// <summary>A complete immutable snapshot prepared on the owning backend before its writer runs.</summary>
public sealed class PreparedOwnerPrompt
{
    public string Machine { get; }
    internal SendOwnerPrompt Request { get; }
    public string ToSessionId => Request.ToSessionId;
    public string ClientId => Request.ClientId;
    public string Text => Request.Text;
    public IReadOnlyList<string> AttachmentIds => Request.AttachmentIds;
    public long? TargetEpoch => Request.TargetEpoch;
    public string AttachmentIdsJson { get; }
    public string ContentSha256 { get; }
    public long TextUtf8Bytes { get; }
    public long AttachmentJsonUtf8Bytes { get; }
    public long AttachmentCount => AttachmentIds.Count;
    internal byte[] StorageTextUtf8 { get; }
    internal string CanonicalJournal { get; }

    internal PreparedOwnerPrompt(string machine, SendOwnerPrompt request, string attachmentIdsJson,
        string contentSha256, long textUtf8Bytes, long attachmentJsonUtf8Bytes, byte[] storageTextUtf8)
    {
        StorageTextUtf8 = storageTextUtf8;
        Machine = machine; Request = request; AttachmentIdsJson = attachmentIdsJson;
        ContentSha256 = contentSha256; TextUtf8Bytes = textUtf8Bytes; AttachmentJsonUtf8Bytes = attachmentJsonUtf8Bytes;
        CanonicalJournal = JsonSerializer.Serialize(new OwnerPromptJournalEnvelope("send_owner_prompt", 1,
            contentSha256, textUtf8Bytes, attachmentJsonUtf8Bytes, AttachmentCount), EngineJson.Default.OwnerPromptJournalEnvelope);
    }
}
