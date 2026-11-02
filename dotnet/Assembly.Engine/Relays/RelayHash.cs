using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Engine.Relays;

/// <summary>Closed new relay digest encoding; existing GUID and foreign-marker encodings stay untouched.</summary>
internal sealed class RelayHash : IDisposable
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal void String(string value) => Bytes(Utf8.GetBytes(value));
    internal void NullableString(string? value)
    {
        _hash.AppendData(value is null ? [0] : [1]);
        if (value is not null) String(value);
    }
    internal void Bytes(ReadOnlySpan<byte> value) { Length(value.Length); Raw(value); }
    internal void Length(long value)
    {
        if (value < 0) throw new RelayRefused("bad_args");
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, checked((ulong)value));
        _hash.AppendData(bytes);
    }
    internal void Flag(bool value) => _hash.AppendData(value ? [1] : [0]);
    internal void Raw(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);
    internal string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());
    internal static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool IsSha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public void Dispose() => _hash.Dispose();
}
