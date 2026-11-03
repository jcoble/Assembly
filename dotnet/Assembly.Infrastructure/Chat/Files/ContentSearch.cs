using System.Runtime.CompilerServices;
using System.Text;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Validates a bounded file as text, then streams lines. A binary suffix cannot turn an earlier line into a false match.</summary>
internal static class ContentSearch
{
    internal sealed record Opened(FileStream Stream, StreamReader Reader) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { Reader.Dispose(); return Stream.DisposeAsync(); }
    }

    internal static async Task<Opened?> OpenAsync(string root, string path, CancellationToken ct)
    {
        FileStream? stream = null;
        try
        {
            var full = SessionPaths.ResolveFile(root, path);
            stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > FileRead.MaxBytes) { await stream.DisposeAsync(); return null; }
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var bytes = new byte[64 * 1024];
            var chars = new char[64 * 1024];
            var total = 0;
            int read;
            do
            {
                ct.ThrowIfCancellationRequested();
                read = await stream.ReadAsync(bytes, ct);
                total += read;
                if (total > FileRead.MaxBytes || bytes.AsSpan(0, read).Contains((byte)0))
                { await stream.DisposeAsync(); return null; }
                decoder.Convert(bytes.AsSpan(0, read), chars, read == 0, out _, out _, out _);
            } while (read != 0);
            stream.Position = 0;
            return new Opened(stream, new StreamReader(new BoundedStream(stream), new UTF8Encoding(false, true), true, 64 * 1024, leaveOpen: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or Assembly.Application.Common.Calls.CallFailed)
        {
            if (stream is not null) await stream.DisposeAsync();
            return null;
        }
        catch { if (stream is not null) await stream.DisposeAsync(); throw; }
    }

    internal static async IAsyncEnumerable<ContentMatch> MatchesAsync(Opened file, string path, string query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var line = 0;
        while (await file.Reader.ReadLineAsync(ct) is { } text)
        {
            ct.ThrowIfCancellationRequested();
            line++;
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var start = Math.Max(0, at - 100);
            if (start > 0 && char.IsLowSurrogate(text[start])) start--;
            var end = Math.Min(text.Length, start + 500);
            if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1])) end--;
            var column = 1;
            foreach (var _ in text[..at].EnumerateRunes()) column++;
            yield return new ContentMatch(path, line, column, text[start..end]);
        }
    }
    private sealed class BoundedStream(Stream inner) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = (int)Math.Min(count, FileRead.MaxBytes + 1L - read);
            if (length <= 0) throw new IOException("the searched file grew past 8 MiB");
            var result = inner.Read(buffer, offset, length);
            read += result;
            if (read > FileRead.MaxBytes) throw new IOException("the searched file grew past 8 MiB");
            return result;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var length = (int)Math.Min(buffer.Length, FileRead.MaxBytes + 1L - read);
            if (length <= 0) throw new IOException("the searched file grew past 8 MiB");
            var result = await inner.ReadAsync(buffer[..length], ct);
            read += result;
            if (read > FileRead.MaxBytes) throw new IOException("the searched file grew past 8 MiB");
            return result;
        }
    }

}
