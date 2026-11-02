using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Application.Common.Engine;

/// <summary>The exact strict UTF8 session-title/v1 content hash shared by title readers and publishers.</summary>
public static class SessionTitleHash
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Compute(ReadOnlySpan<byte> utf8Title)
    {
        _ = Utf8.GetCharCount(utf8Title);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("session-title/v1"u8);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)utf8Title.Length);
        hash.AppendData(length);
        hash.AppendData(utf8Title);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
