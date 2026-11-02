using System.Security.Cryptography;
using Assembly.Application.Common.Calls;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine.Relays;

/// <summary>Sequential raw UTF8 segments; limits a frame rather than the complete message.</summary>
internal sealed class RelaySegments(RelayPartManifest manifest) : IDisposable
{
    internal const int ChunkBytes = 1048576;
    private readonly List<byte[]> _segments = [];
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal long Length { get; private set; }
    internal void Append(long offset, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset != Length || bytes.Length is < 1 or > ChunkBytes || Length > manifest.Utf8Bytes - bytes.Length)
            throw new CallFailed("bad_parts");
        var owned = bytes.ToArray();
        token.ThrowIfCancellationRequested();
        _segments.Add(owned); _hash.AppendData(owned); Length += owned.Length;
    }
    internal byte[] Complete(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Length != manifest.Utf8Bytes || Convert.ToHexStringLower(_hash.GetCurrentHash()) != manifest.Sha256)
            throw new CallFailed("bad_parts");
        // The existing prepared prompt and SQLite binding both require complete owned arrays.
        var bytes = new byte[checked((int)Length)];
        var offset = 0;
        foreach (var segment in _segments)
        { token.ThrowIfCancellationRequested(); segment.CopyTo(bytes, offset); offset += segment.Length; }
        token.ThrowIfCancellationRequested();
        return bytes;
    }
    public void Dispose() { _segments.Clear(); _hash.Dispose(); }
}
