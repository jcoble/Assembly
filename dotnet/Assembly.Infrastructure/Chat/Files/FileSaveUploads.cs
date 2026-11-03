using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Four bounded private staging files per connection. A failed operation or closed owner ends its upload.</summary>
public sealed class FileSaveUploads
{
    private sealed class Upload(string id, string connection, string root, BeginSave args, string stage, long? replyId)
    {
        public readonly string Id = id;
        public readonly long? ReplyId = replyId;
        public readonly string Connection = connection;
        public readonly string Root = root;
        public readonly BeginSave Args = args;
        public readonly string Stage = stage;
        public readonly SemaphoreSlim Gate = new(1);
        public long Offset;
        public volatile bool Ended;
        public readonly CancellationTokenSource Closing = new();
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Upload> uploads = [];

    public Task<BegunSave> BeginAsync(string root, BeginSave args, string connection, CancellationToken ct, long? replyId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Size < 0 || args.Size > FileRead.MaxBytes || args.ExpectedRevision.Length != 64)
            throw new CallFailed("invalid save size or base revision");
        var full = SessionPaths.ResolveFile(root, args.Path);
        if (!File.Exists(full)) throw new CallFailed("the target file is missing");
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Path.GetDirectoryName(full)!, ".assembly-save-" + id);
        lock (gate)
        {
            if (uploads.Values.Count(upload => upload.Connection == connection) >= 4)
                throw new CallFailed("four file uploads are already open on this connection");
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            uploads.Add(id, new Upload(id, connection, root, args, stage, replyId));
        }
        return Task.FromResult(new BegunSave(id));
    }

    public async Task AppendAsync(SaveChunk args, string connection, CancellationToken ct)
    {
        var upload = Find(args.Upload, connection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, upload.Closing.Token);
        ct = linked.Token;
        try { await upload.Gate.WaitAsync(ct); }
        catch { Drop(id: upload.Id, connection); throw; }
        try
        {
            if (upload.Ended) throw new CallFailed("this save upload has ended");
            ct.ThrowIfCancellationRequested();
            if (args.Data.Length > FileRead.ChunkBytes || args.Offset != upload.Offset ||
                args.Data.Length > upload.Args.Size - upload.Offset)
                throw new CallFailed("invalid save chunk size or offset");
            await using var stream = new FileStream(upload.Stage, FileMode.Open, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            stream.Position = upload.Offset;
            await stream.WriteAsync(args.Data, ct);
            await stream.FlushAsync(ct);
            upload.Offset += args.Data.Length;
        }
        catch { End(upload); throw; }
        finally { FinishTurn(upload); }
    }

    internal async Task<SaveResult> CommitAsync(string id, string connection, CancellationToken ct,
        Action<string, string>? validateOwner = null)
    {
        var upload = Find(id, connection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, upload.Closing.Token);
        ct = linked.Token;
        try { await upload.Gate.WaitAsync(ct); }
        catch { Drop(id: upload.Id, connection); throw; }
        try
        {
            if (upload.Ended || upload.Offset != upload.Args.Size) throw new CallFailed("the save upload is not complete");
            ct.ThrowIfCancellationRequested();
            return await FileSave.CommitStageAsync(upload.Root, upload.Args.Path, upload.Args.ExpectedRevision, upload.Stage, ct,
                validateOwner is null ? null : () => validateOwner(upload.Args.Session, upload.Root));
        }
        finally { End(upload); FinishTurn(upload); }
    }

    public void Drop(string id, string connection)
    {
        Upload? upload;
        lock (gate)
        {
            if (!uploads.TryGetValue(id, out upload) || upload.Connection != connection) return;
            upload.Ended = true;
            uploads.Remove(id);
        }
        upload.Closing.Cancel();
        if (upload.Gate.Wait(0)) FinishTurn(upload);
    }

    internal string? OrderKey(string id)
    {
        lock (gate) return uploads.TryGetValue(id, out var upload)
            ? "files:" + SessionPaths.ResolveFile(upload.Root, upload.Args.Path) : null;
    }

    internal void FailedBegin(string connection, long? replyId)
    {
        string? id;
        lock (gate) id = uploads.Values.FirstOrDefault(upload => upload.Connection == connection && upload.ReplyId == replyId)?.Id;
        if (id is not null) Drop(id, connection);
    }

    public void Close(string connection)
    {
        string[] ids;
        lock (gate) ids = uploads.Values.Where(upload => upload.Connection == connection).Select(upload => upload.Id).ToArray();
        foreach (var id in ids) Drop(id, connection);
    }

    private Upload Find(string id, string connection)
    {
        lock (gate)
            return uploads.TryGetValue(id, out var upload) && upload.Connection == connection && !upload.Ended
                ? upload : throw new CallFailed("unknown save upload on this connection");
    }

    private void End(Upload upload)
    {
        lock (gate) { upload.Ended = true; uploads.Remove(upload.Id); }
    }

    private static void FinishTurn(Upload upload)
    {
        try { if (upload.Ended && File.Exists(upload.Stage)) File.Delete(upload.Stage); }
        finally { upload.Gate.Release(); }
    }
}
