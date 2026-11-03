using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Remote;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Chat.Attachments;

/// <summary>
/// Images over the socket (plan D4, option A): <c>attachments/begin</c> checks the type and size and opens an upload,
/// <c>attachments/chunk</c> appends up to <see cref="ChunkBytes"/> at its offset to the upload's staging file, and
/// <c>attachments/commit</c> checks the bytes and saves them where the session's agent runs, answering the path there.
/// <c>attachments/read</c> reads the owning machine's image for the preview, a piece at a time. Staging files live under the
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
    private readonly string _staging;
    private readonly IRowSourceBindings? _sources;
    private readonly Lock _gate = new();
    // Each open upload, by its id.
    private readonly Dictionary<string, Upload> _uploads = [];

    public AttachmentsFeature(SessionStore sessions, MachineSet machines, string staging, IRowSourceBindings? sources = null)
    {
        _sessions = sessions;
        _machines = machines;
        _staging = staging;
        _sources = sources;
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
        calls.Write(Feature, "begin", AttachmentsJson.Default.BeginArgs, AttachmentsJson.Default.Begun, async (args, call) =>
        {
            var source = SendFeature.Source(args.ExpectedSource, call);
            call.Cancellation.ThrowIfCancellationRequested();
            call.Closed.ThrowIfCancellationRequested();
            var lifetime = await Capture(args.Session, source, call);
            var session = lifetime?.Captured.Session ?? _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var type = ImageType.ForContentType(args.Mime) ?? throw new CallFailed("expected a png, jpeg, gif or webp image");
            if (args.Size is < 1 or > AttachmentStore.MaxBytes) throw new CallFailed("images must be 1 byte to 20 MB");
            return scope =>
            {
                Before(lifetime, call, scope);
                var id = Guid.NewGuid().ToString();
                var path = Path.Combine(_staging, id);
                Upload upload;
                lock (_gate)
                {
                    if (_uploads.Values.Count(upload => upload.Connection == call.Connection) >= MaxOpen)
                        throw new CallFailed($"at most {MaxOpen} uploads are open at once; commit or drop one");
                    upload = new Upload(call, session, type, args.Size, path, lifetime);
                    _uploads[id] = upload;
                }
                Directory.CreateDirectory(_staging);
                File.Create(path).Dispose();
                upload.Start(() => Drop(upload));
                return new Begun(id);
            };
        }, args => SendFeature.OrderOf(args.Session), (_, call) =>
        {
            Upload? opened;
            lock (_gate) opened = _uploads.Values.FirstOrDefault(upload => ReferenceEquals(upload.Opener, call));
            if (opened is not null) Drop(opened);
        }, bulk: true);
        calls.Write(Feature, "chunk", AttachmentsJson.Default.ChunkArgs, AttachmentsJson.Default.Received, async (args, call) =>
        {
            var upload = Find(args.Upload, call);
            using var operation = upload.Enter();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, upload.Stopped);
            try
            {
                Before(upload.Source, call);
                stop.Token.ThrowIfCancellationRequested();
                if (args.Data.Length > ChunkBytes) throw new CallFailed($"a chunk carries at most {ChunkBytes} bytes");
                if (args.Offset != upload.Written) throw new CallFailed($"expected the chunk at {upload.Written}");
                if (upload.Written + args.Data.Length > upload.Size) throw new CallFailed("the chunk passes the size begin declared");
                await using (var file = new FileStream(upload.Path, FileMode.Append, FileAccess.Write))
                    await file.WriteAsync(args.Data, stop.Token);
                upload.Written += args.Data.Length;
                Before(upload.Source, call);
                var written = upload.Written;
                return scope => { Before(upload.Source, call, scope); return new Received(written); };
            }
            catch { Drop(upload); throw; }
        }, args => OrderOf(args.Upload), (args, call) => DropIfOpen(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "commit", AttachmentsJson.Default.CommitArgs, AttachmentsJson.Default.Saved, async (args, call) =>
        {
            var upload = Find(args.Upload, call);
            using var operation = upload.Enter();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, upload.Stopped);
            try
            {
                Before(upload.Source, call);
                stop.Token.ThrowIfCancellationRequested();
                if (upload.Written != upload.Size) throw new CallFailed($"the upload has {upload.Written} of its {upload.Size} bytes");
                var bytes = await File.ReadAllBytesAsync(upload.Path, stop.Token);
                if (!upload.Type.Matches(bytes)) throw new CallFailed("the bytes are not the image type sent");
                Before(upload.Source, call);
                var captured = upload.Source?.Captured;
                var path = captured?.Machine is RemoteMachine remote
                    ? await remote.SaveAttachmentAsync(upload.Session, upload.Type, bytes, stop.Token, captured.Live)
                    : await (captured?.Machine ?? _machines.For(upload.Session)).SaveAttachmentAsync(upload.Session, upload.Type, bytes, stop.Token);
                // File publication can precede a source change. Never retarget or claim that accepted file was rolled back.
                return scope =>
                {
                    try { Before(upload.Source, call, scope); }
                    catch (Exception error) when (upload.Source is not null && error is CallFailed or OperationCanceledException)
                    { throw new CallUncertain(upload.Source.Machine, "The image may have been saved before its source changed. The draft is preserved; do not retry automatically."); }
                    return new Saved(path);
                };
            }
            finally { Drop(upload); }
        }, args => OrderOf(args.Upload), (args, call) => DropIfOpen(args.Upload, call.Connection), bulk: true);
        calls.Write(Feature, "drop", AttachmentsJson.Default.DropArgs, AttachmentsJson.Default.Dropped, (args, call) =>
        {
            DropIfOpen(args.Upload, call.Connection);
            return Task.FromResult<Func<IWriteScope, Dropped>>(_ => new Dropped(true));
        }, args => OrderOf(args.Upload), bulk: true);
        calls.Read(Feature, "read", AttachmentsJson.Default.ReadArgs, AttachmentsJson.Default.AttachmentPiece, async (args, call) =>
        {
            var source = SendFeature.Source(args.ExpectedSource, call);
            call.Cancellation.ThrowIfCancellationRequested();
            var lifetime = await Capture(args.Session, source, call);
            var captured = lifetime?.Captured;
            var session = captured?.Session ?? _sessions.Get(args.Session) ?? throw new CallFailed("no such attachment");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, lifetime?.Changed ?? CancellationToken.None);
            Before(lifetime, call);
            var piece = captured?.Machine is RemoteMachine remote
                ? await remote.ReadAttachmentAsync(session, args.Name, args.Offset, stop.Token, captured.Live)
                : await (captured?.Machine ?? _machines.For(session)).ReadAttachmentAsync(session, args.Name, args.Offset, stop.Token);
            Before(lifetime, call);
            return piece;
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

    private async Task<SourceLifetime?> Capture(string session, ExpectedSource? source, CallContext call)
    {
        if (source is null) return null;
        if (_sources is not SessionRowSources actual) throw new CallFailed("The selected attachment source changed.", "source_changed");
        var captured = await actual.CaptureCommandAsync(source, session, call.Cancellation);
        var lifetime = new SourceLifetime(captured, source.Machine);
        call.SourceGuard = lifetime.Initial();
        Before(lifetime, call);
        return lifetime;
    }

    private static void Before(SourceLifetime? source, CallContext call, IReadScope? scope = null)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        call.Closed.ThrowIfCancellationRequested();
        source?.Captured.Guard.EnsureDisplayCurrent(scope);
    }

    // Another socket's upload is unknown. A sourced upload carries its original source through every later call.
    private Upload Find(string id, CallContext call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        Upload upload;
        lock (_gate) upload = _uploads.TryGetValue(id, out var found) && found.Connection == call.Connection
            ? found : throw new CallFailed("unknown upload");
        if (upload.Source is { } source)
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
            call.SourceGuard = source.Acquire();
        }
        return upload;
    }

    private void DropIfOpen(string id, string connection)
    {
        Upload? upload;
        lock (_gate) upload = _uploads.GetValueOrDefault(id);
        if (upload?.Connection == connection) Drop(upload);
    }

    private string? OrderOf(string id)
    {
        lock (_gate) return _uploads.TryGetValue(id, out var upload) ? SendFeature.OrderOf(upload.Session.SessionId) : null;
    }

    private void Drop(Upload upload)
    {
        lock (_gate) _uploads.Remove(upload.Id);
        upload.Close();
    }

    // The transfer and each queued reply own separate references to the same certified source.
    private sealed class SourceLifetime(SessionRowSources.CommandBinding captured, string machine)
    {
        private readonly Lock _gate = new();
        private int _references = 1;
        public SessionRowSources.CommandBinding Captured => captured;
        public string Machine => machine;
        public CancellationToken Changed => captured.Guard.Changed;
        public IExpectedSourceGuard Initial() => new Lease(this);
        public IExpectedSourceGuard Acquire()
        {
            lock (_gate)
            {
                if (_references == 0) throw new CallFailed("The attachment source closed.", "source_changed");
                _references++;
                return new Lease(this);
            }
        }
        private void Release()
        {
            bool dispose;
            lock (_gate) dispose = --_references == 0;
            if (dispose) captured.Guard.Dispose();
        }
        private sealed class Lease(SourceLifetime owner) : IExpectedSourceGuard
        {
            private int _disposed;
            public CancellationToken Changed => owner.Changed;
            public void EnsureCurrent() => owner.Captured.Guard.EnsureDisplayCurrent();
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(); }
        }
    }

    private sealed class Upload
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _stop;
        private readonly IExpectedSourceGuard? _sourceOwner;
        private CancellationTokenRegistration _registration;
        private int _active;
        private bool _closed, _cleaned;
        public CallContext Opener { get; }
        public string Connection => Opener.Connection;
        public SessionInfo Session { get; }
        public ImageType Type { get; }
        public long Size { get; }
        public string Path { get; }
        public string Id => System.IO.Path.GetFileName(Path);
        public SourceLifetime? Source { get; }
        public CancellationToken Stopped { get; }
        public long Written { get; set; }
        public Upload(CallContext opener, SessionInfo session, ImageType type, long size, string path, SourceLifetime? source)
        {
            (Opener, Session, Type, Size, Path, Source) = (opener, session, type, size, path, source);
            _sourceOwner = source?.Acquire();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(opener.Closed, source?.Changed ?? CancellationToken.None);
            Stopped = _stop.Token;
        }
        public void Start(Action drop)
        {
            var registration = Stopped.Register(drop);
            bool dispose;
            lock (_gate) { dispose = _closed; if (!dispose) _registration = registration; }
            if (dispose) registration.Dispose();
        }
        public IDisposable Enter()
        {
            lock (_gate)
            {
                if (_closed) throw new CallFailed("unknown upload");
                _active++;
            }
            return new Operation(this);
        }
        public void Close()
        {
            lock (_gate) { if (_closed) return; _closed = true; }
            _stop.Cancel();
            Cleanup();
        }
        private void Cleanup()
        {
            lock (_gate) { if (!_closed || _active != 0 || _cleaned) return; _cleaned = true; }
            try { File.Delete(Path); }
            finally { _registration.Dispose(); _stop.Dispose(); _sourceOwner?.Dispose(); }
        }
        private sealed class Operation(Upload owner) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (owner._gate) owner._active--;
                owner.Cleanup();
            }
        }
    }

    internal sealed record BeginArgs([property: JsonRequired] string Session, [property: JsonRequired] string Mime,
        [property: JsonRequired] long Size, ExpectedSource? ExpectedSource = null);

    internal sealed record Begun(string Upload);

    internal sealed record ChunkArgs([property: JsonRequired] string Upload, [property: JsonRequired] long Offset,
        [property: JsonRequired] byte[] Data);

    internal sealed record Received(long Bytes);

    internal sealed record CommitArgs([property: JsonRequired] string Upload);

    internal sealed record Saved(string Path);

    internal sealed record DropArgs([property: JsonRequired] string Upload);

    internal sealed record Dropped(bool Ok);

    internal sealed record ReadArgs([property: JsonRequired] string Session, [property: JsonRequired] string Name, long Offset = 0, ExpectedSource? ExpectedSource = null);
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
[JsonSerializable(typeof(AttachmentPiece))]
internal sealed partial class AttachmentsJson : JsonSerializerContext;
