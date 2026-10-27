using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Chat.Attachments;

/// <summary>
/// Images over the socket (plan D4, option A): <c>attachments/begin</c> checks the type and size and opens an upload,
/// <c>attachments/chunk</c> appends up to <see cref="ChunkBytes"/> at its offset to the upload's staging file, and
/// <c>attachments/commit</c> checks the bytes and saves them where the session's agent runs, answering the path there.
/// <c>attachments/read</c> reads this machine's copy back for the preview, a piece at a time. Staging files live under the
/// backend's home, one per upload, never past the size begin declared; a failed chunk or commit, or the socket closing
/// once its writes have run, drops the upload and its file.
/// </summary>
internal sealed class AttachmentsFeature : IFeature
{
    public const string Feature = "attachments";

    /// <summary>The most bytes a chunk carries, or a read answers.</summary>
    public const int ChunkBytes = 1024 * 1024;

    private readonly SessionStore _sessions;
    private readonly MachineSet _machines;
    private readonly AttachmentStore _store;
    private readonly string _staging;

    public AttachmentsFeature(SessionStore sessions, MachineSet machines, AttachmentStore store, string staging)
    {
        _sessions = sessions;
        _machines = machines;
        _store = store;
        _staging = staging;
    }

    public void Register(ICallRegistry calls)
    {
    }

    internal sealed record BeginArgs([property: JsonRequired] string Session, [property: JsonRequired] string Mime,
        [property: JsonRequired] long Size);

    internal sealed record Begun(string Upload);

    internal sealed record ChunkArgs([property: JsonRequired] string Upload, [property: JsonRequired] long Offset,
        [property: JsonRequired] byte[] Data);

    internal sealed record Received(long Bytes);

    internal sealed record CommitArgs([property: JsonRequired] string Upload);

    internal sealed record Saved(string Path);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] string Name, long Offset = 0);

    internal sealed record Piece(byte[] Data, long Size, string Mime);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AttachmentsFeature.BeginArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Begun))]
[JsonSerializable(typeof(AttachmentsFeature.ChunkArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Received))]
[JsonSerializable(typeof(AttachmentsFeature.CommitArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Saved))]
[JsonSerializable(typeof(AttachmentsFeature.ReadArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Piece))]
internal sealed partial class AttachmentsJson : JsonSerializerContext;
