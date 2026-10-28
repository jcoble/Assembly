using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Chat.Attachments;

/// <summary>
/// Images over the socket (plan D4, option A): <c>attachments/begin</c> checks the type and size and opens an upload,
/// <c>attachments/chunk</c> appends up to <see cref="ChunkBytes"/> at its offset to the upload's staging file, and
/// <c>attachments/commit</c> checks the bytes and saves them where the session's agent runs, answering the path there.
/// <c>attachments/read</c> reads this machine's copy back for the preview, a piece at a time. Staging files live under the
/// backend's home, one per upload, never past the size begin declared, and a socket has at most <see cref="MaxOpen"/>
/// open. <c>attachments/drop</c>, a failed or cancelled chunk or commit, or the socket closing once its writes have run,
/// drops the upload and its file.
/// </summary>
internal sealed class AttachmentsFeature : IFeature
{
    public const string Feature = "attachments";

    /// <summary>The most bytes a chunk carries, or a read answers.</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>The most uploads one socket has open at once.</summary>
    public const int MaxOpen = 8;

    private readonly SessionStore _sessions;
    private readonly MachineSet _machines;
    private readonly AttachmentStore _store;
    private readonly string _staging;
    private readonly Lock _gate = new();
    // Each open upload, by its id.
    private readonly Dictionary<string, Upload> _uploads = [];

    public AttachmentsFeature(SessionStore sessions, MachineSet machines, AttachmentStore store, string staging)
    {
        _sessions = sessions;
        _machines = machines;
        _store = store;
        _staging = staging;
        // What a backend that stopped mid-upload left: only files begin names, so a home pointed at a real folder loses
        // nothing else.
        if (Directory.Exists(staging))
            foreach (var file in Directory.GetFiles(staging).Where(file => Guid.TryParse(System.IO.Path.GetFileName(file), out _)))
                File.Delete(file);
    }

    // begin, chunk, commit and drop are writes keyed on the upload's session, so they run in order, apart from a machine that
    // hangs elsewhere, and a closing socket doesn't cancel one it queued.
    public void Register(ICallRegistry calls)
    {
        calls.Write(Feature, "begin", AttachmentsJson.Default.BeginArgs, AttachmentsJson.Default.Begun, (args, call) =>
        {
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var type = ImageType.ForContentType(args.Mime) ?? throw new CallFailed("expected a png, jpeg, gif or webp image");
            if (args.Size is < 1 or > AttachmentStore.MaxBytes)
                throw new CallFailed("images must be 1 byte to 20 MB");
            // Opened in the database part, past the last cancel check; if the write still answers an error, failed drops it,
            // so a begin that doesn't answer its id leaves no upload the window can't name.
            return Task.FromResult<Func<IWriteScope, Begun>>(_ =>
            {
                var id = Guid.NewGuid().ToString();
                var path = Path.Combine(_staging, id);
                lock (_gate)
                {
                    if (_uploads.Values.Count(upload => upload.Connection == call.Connection) >= MaxOpen)
                        throw new CallFailed($"at most {MaxOpen} uploads are open at once; commit or drop one");
                    _uploads[id] = new Upload(call, session, type, args.Size, path);
                }
                Directory.CreateDirectory(_staging);
                File.Create(path).Dispose();
                return new Begun(id);
            });
        }, args => SendFeature.OrderOf(args.Session), (_, call) =>
        {
            Upload? opened;
            lock (_gate)
                opened = _uploads.Values.FirstOrDefault(upload => ReferenceEquals(upload.Opener, call));
            if (opened is not null)
                Drop(opened);
        }, bulk: true);
        calls.Write(Feature, "chunk", AttachmentsJson.Default.ChunkArgs, AttachmentsJson.Default.Received, async (args, call) =>
        {
            var upload = Find(args.Upload, call.Connection);
            try
            {
                if (args.Data.Length > ChunkBytes)
                    throw new CallFailed($"a chunk carries at most {ChunkBytes} bytes");
                if (args.Offset != upload.Written)
                    throw new CallFailed($"expected the chunk at {upload.Written}");
                if (upload.Written + args.Data.Length > upload.Size)
                    throw new CallFailed("the chunk passes the size begin declared");
                await using (var file = new FileStream(upload.Path, FileMode.Append, FileAccess.Write))
                    await file.WriteAsync(args.Data, call.Cancellation);
                upload.Written += args.Data.Length;
            }
            catch
            {
                Drop(upload);
                throw;
            }
            var written = upload.Written;
            return _ => new Received(written);
        }, args => OrderOf(args.Upload), (args, call) => DropIfOpen(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "commit", AttachmentsJson.Default.CommitArgs, AttachmentsJson.Default.Saved, async (args, call) =>
        {
            var upload = Find(args.Upload, call.Connection);
            try
            {
                // The window that started it is gone, and a reconnect starts a fresh upload, so its bytes go to no machine.
                if (call.Closed.IsCancellationRequested)
                    throw new CallFailed("the socket closed before the commit ran");
                if (upload.Written != upload.Size)
                    throw new CallFailed($"the upload has {upload.Written} of its {upload.Size} bytes");
                var bytes = await File.ReadAllBytesAsync(upload.Path, call.Cancellation);
                if (!upload.Type.Matches(bytes))
                    throw new CallFailed("the bytes are not the image type sent");
                var path = await _machines.For(upload.Session).SaveAttachmentAsync(upload.Session, upload.Type, bytes, call.Cancellation);
                return _ => new Saved(path);
            }
            finally
            {
                Drop(upload);
            }
        }, args => OrderOf(args.Upload), (args, call) => DropIfOpen(args.Upload, call.Connection), bulk: true);
        // For an upload the window gave up on with no call in flight. An unknown or dropped upload is fine.
        calls.Write(Feature, "drop", AttachmentsJson.Default.DropArgs, AttachmentsJson.Default.Dropped, (args, call) =>
        {
            DropIfOpen(args.Upload, call.Connection);
            return Task.FromResult<Func<IWriteScope, Dropped>>(_ => new Dropped(true));
        }, args => OrderOf(args.Upload), bulk: true);
        calls.Read(Feature, "read", AttachmentsJson.Default.ReadArgs, AttachmentsJson.Default.Piece, async (args, call) =>
        {
            var type = AttachmentStore.TypeOfName(args.Name) ?? throw new CallFailed("not an attachment name");
            var path = _store.PathOf(args.Session, args.Name);
            if (_sessions.Get(args.Session) is null || !File.Exists(path))
                throw new CallFailed("no such attachment");
            await using var file = File.OpenRead(path);
            if (args.Offset < 0 || args.Offset > file.Length)
                throw new CallFailed("the offset is outside the attachment");
            file.Position = args.Offset;
            var data = new byte[Math.Min(ChunkBytes, file.Length - args.Offset)];
            await file.ReadExactlyAsync(data, call.Cancellation);
            return new Piece(data, file.Length, type.ContentType);
        }, bulk: true);
        // Runs once the socket's queued chunks have all run, so none writes to a file after it's deleted.
        calls.Closed(connection =>
        {
            List<Upload> left;
            lock (_gate)
                left = [.. _uploads.Values.Where(upload => upload.Connection == connection)];
            foreach (var upload in left)
                Drop(upload);
        });
    }

    // Another socket's upload is unknown here.
    private Upload Find(string id, string connection)
    {
        lock (_gate)
            return _uploads.TryGetValue(id, out var upload) && upload.Connection == connection ? upload : throw new CallFailed("unknown upload");
    }

    private void DropIfOpen(string id, string connection)
    {
        Upload? upload;
        lock (_gate)
            upload = _uploads.GetValueOrDefault(id);
        if (upload?.Connection == connection)
            Drop(upload);
    }

    // An unknown upload's call has no key: it fails at once on the socket's queue.
    private string? OrderOf(string id)
    {
        lock (_gate)
            return _uploads.TryGetValue(id, out var upload) ? SendFeature.OrderOf(upload.Session.SessionId) : null;
    }

    private void Drop(Upload upload)
    {
        lock (_gate)
            _uploads.Remove(upload.Id);
        File.Delete(upload.Path);
    }

    private sealed class Upload(CallContext opener, SessionInfo session, ImageType type, long size, string path)
    {
        public string Id => System.IO.Path.GetFileName(path);

        /// <summary>The begin that opened it.</summary>
        public CallContext Opener => opener;
        public string Connection => opener.Connection;
        public SessionInfo Session => session;
        public ImageType Type => type;
        public long Size => size;
        public string Path => path;

        /// <summary>The bytes appended so far. Only its upload's writes, which run one at a time, change it.</summary>
        public long Written { get; set; }
    }

    internal sealed record BeginArgs([property: JsonRequired] string Session, [property: JsonRequired] string Mime,
        [property: JsonRequired] long Size);

    internal sealed record Begun(string Upload);

    internal sealed record ChunkArgs([property: JsonRequired] string Upload, [property: JsonRequired] long Offset,
        [property: JsonRequired] byte[] Data);

    internal sealed record Received(long Bytes);

    internal sealed record CommitArgs([property: JsonRequired] string Upload);

    internal sealed record Saved(string Path);

    internal sealed record DropArgs([property: JsonRequired] string Upload);

    internal sealed record Dropped(bool Ok);

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
[JsonSerializable(typeof(AttachmentsFeature.DropArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Dropped))]
[JsonSerializable(typeof(AttachmentsFeature.ReadArgs))]
[JsonSerializable(typeof(AttachmentsFeature.Piece))]
internal sealed partial class AttachmentsJson : JsonSerializerContext;
