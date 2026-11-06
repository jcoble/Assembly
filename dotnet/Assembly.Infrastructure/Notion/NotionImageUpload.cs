using Assembly.Application.Common.Images;
using System.Collections.Concurrent;
using System.Text.Json;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Notion;

public sealed class NotionImageUpload(string root, NotionHttp http, NotionSettings settings, NotionEdits edits, TimeProvider? time = null) : IDisposable, IAsyncDisposable
{
    public const int ChunkBytes = 64 * 1024;
    /// An upload its client went quiet on, say a lease answered after the client stopped listening, is let go by then.
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();
    private readonly object _gate = new();
    private bool _disposed;
    private sealed class Upload(string connection, NotionTaskRef source, ImageType type, long size, long generation, string path, DateTimeOffset now)
    {
        public DateTimeOffset LastUsed = now;
        public string Connection = connection;
        public NotionTaskRef Source = source;
        public ImageType Type = type;
        public long Size = size, Generation = generation, Offset;
        public string Path = path;
        public string? NotionUploadId;
        public CancellationTokenSource Stop = new();
        public SemaphoreSlim Gate = new(1);
        public bool Committing;
        public TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public Task<NotionImageLease> BeginAsync(string connection, NotionTaskRef source, string mime, long size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = settings.Read();
        source = new(NotionIds.Canonical(source.DataSourceId), NotionIds.Canonical(source.PageId));
        if (!current.HasToken || current.DataSourceId != source.DataSourceId) throw new NotionException("notion_source_mismatch");
        var type = ImageType.ForContentType(mime) ?? throw new NotionException("unsupported_image_type");
        if (size is < 1 or > 20 * 1024 * 1024) throw new NotionException("image_too_large");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetUtcNow();
            foreach (var pair in _uploads)
                if (pair.Value.Connection == connection && !pair.Value.Committing && now - pair.Value.LastUsed > IdleLimit) Drop(connection, pair.Key);
            if (_uploads.Values.Count(x => x.Connection == connection) >= 2) throw new NotionException("upload_capacity");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var id = Guid.NewGuid().ToString("D");
            var path = System.IO.Path.Combine(root, id + ".part");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(path, options)) { }
            _uploads[id] = new(connection, source, type, size, current.Generation, path, now);
            return Task.FromResult(new NotionImageLease(id, ChunkBytes));
        }
    }
    private Upload Find(string connection, string id) => _uploads.TryGetValue(id, out var upload) && upload.Connection == connection ? upload : throw new NotionException("unknown_upload");
    public async Task<NotionEmpty> ChunkAsync(string connection, string id, long offset, string data, CancellationToken ct)
    {
        var upload = Find(connection, id);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, upload.Stop.Token);
        await upload.Gate.WaitAsync(linked.Token);
        try
        {
            upload.LastUsed = _time.GetUtcNow();
            if (upload.Committing || offset != upload.Offset) throw new NotionException("invalid_upload_offset");
            if (data.Length > 4 * ((ChunkBytes + 2) / 3) || JsonSerializer.SerializeToUtf8Bytes(new NotionImageChunk(id, offset, data), NotionJson.Default.NotionImageChunk).Length > 576 * 1024 - 1024) throw new NotionException("image_chunk_too_large");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); } catch (FormatException) { throw new NotionException("invalid_image_chunk"); }
            if (bytes.Length > ChunkBytes || offset + bytes.Length > upload.Size) throw new NotionException("image_chunk_too_large");
            await using var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            file.Position = offset;
            try { await file.WriteAsync(bytes, linked.Token); await file.FlushAsync(linked.Token); }
            catch { file.SetLength(offset); throw; }
            upload.Offset += bytes.Length;
            upload.LastUsed = _time.GetUtcNow();
            return new();
        }
        catch (OperationCanceledException) { Drop(connection, id); throw; }
        finally { upload.Gate.Release(); }
    }
    public async Task<NotionEditReceipt> CommitAsync(string connection, string id, CancellationToken ct)
    {
        var upload = Find(connection, id);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, upload.Stop.Token);
        await upload.Gate.WaitAsync(linked.Token);
        try
        {
            if (upload.Committing || upload.Offset != upload.Size) throw new NotionException("image_incomplete");
            upload.Committing = true;
            settings.Check(upload.Generation);
            await using (var input = new FileStream(upload.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous))
            {
                var header = new byte[Math.Min(12, upload.Size)];
                await input.ReadExactlyAsync(header, linked.Token);
                if (!upload.Type.Matches(header)) throw new NotionException("image_type_mismatch");
            }
            linked.Token.ThrowIfCancellationRequested();
            var ready = System.IO.Path.ChangeExtension(upload.Path, "ready");
            lock (_gate) { linked.Token.ThrowIfCancellationRequested(); File.Move(upload.Path, ready); upload.Path = ready; }
            var page = await edits.Preflight(upload.Source, null, linked.Token);
            var revision = page.GetProperty("last_edited_time").GetString()!;
            var create = NotionPayload.Build(w => { w.WriteString("mode", "single_part"); w.WriteString("filename", "screenshot." + upload.Type.Extension); w.WriteString("content_type", upload.Type.ContentType); });
            var created = await http.SendAsync(HttpMethod.Post, "/v1/file_uploads", create, linked.Token);
            upload.NotionUploadId = NotionIds.Canonical(created.GetProperty("id").GetString()!);
            await using var file = new FileStream(ready, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, ChunkBytes, FileOptions.Asynchronous);
            await http.SendUploadAsync(upload.NotionUploadId, file, upload.Type.ContentType, linked.Token);
            settings.Check(upload.Generation);
            var body = NotionPayload.Build(w =>
            {
                w.WritePropertyName("children"); w.WriteStartArray(); w.WriteStartObject(); w.WriteString("object", "block"); w.WriteString("type", "image");
                w.WritePropertyName("image"); w.WriteStartObject(); w.WriteString("type", "file_upload"); w.WritePropertyName("file_upload"); w.WriteStartObject(); w.WriteString("id", upload.NotionUploadId);
                w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject(); w.WriteEndArray();
            });
            return await edits.Mutate(upload.Source.PageId, "/v1/blocks/" + upload.Source.PageId + "/children", body, revision, linked.Token, 1);
        }
        finally { Drop(connection, id); upload.Done.TrySetResult(); upload.Gate.Release(); }
    }
    public void Drop(string connection, string id)
    {
        lock (_gate)
        {
            if (!_uploads.TryGetValue(id, out var upload) || upload.Connection != connection) return;
            if (!_uploads.TryRemove(id, out _)) return;
            upload.Stop.Cancel();
            File.Delete(upload.Path);
            if (!upload.Committing) upload.Done.TrySetResult();
        }
    }
    public void Close(string connection)
    {
        foreach (var pair in _uploads) if (pair.Value.Connection == connection) Drop(connection, pair.Key);
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; foreach (var pair in _uploads) Drop(pair.Value.Connection, pair.Key); }
    }
    public async ValueTask DisposeAsync()
    {
        var finishing = _uploads.Values.Select(x => x.Done.Task).ToArray();
        Dispose();
        await Task.WhenAll(finishing);
    }
}
