using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Engine.Persistence;

internal static class RelayReceiptKeys
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string ForeignParent(string target, string node) =>
        "foreign-parent/v1/" + Digest("foreign-parent-slot/v1", target, node);

    internal static Guid ContextGuid(string key) => new(Convert.FromHexString(Digest("private-command-context/v1", key)).AsSpan(0, 16));

    internal static string Digest(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            // -1 is an explicit null; every string, including empty, has its own length.
            var bytes = field is null ? null : Utf8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            hash.AppendData(length);
            if (bytes is not null) hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
