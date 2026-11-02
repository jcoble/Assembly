using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Persistence;

/// <summary>Exact generated canonical bytes for the two prepared title/helper string fields.</summary>
internal sealed record PreparedTextJson(string Kind, string? SmallCanonical, string Sha256, long Utf8Bytes)
{
    internal static PreparedTextJson Create(EngineCommand emptyCommand, string property, string? value)
    {
        if (property is not ("title" or "text")) throw new ArgumentOutOfRangeException(nameof(property));
        var empty = JsonSerializer.SerializeToUtf8Bytes(emptyCommand, EngineJson.Default.EngineCommand);
        if (empty.Length > 32 * 1024) throw new ArgumentException("command_too_large");
        var reader = new Utf8JsonReader(empty);
        var start = -1; var end = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(property)) continue;
            if (start != -1 || !reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() != "")
                throw new InvalidOperationException("Prepared command requires one empty generated string field.");
            start = checked((int)reader.TokenStartIndex); end = checked((int)reader.BytesConsumed);
        }
        if (start < 0) throw new InvalidOperationException("Prepared command string field is missing.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var prefix = new MemoryStream(32 * 1024);
        long length = 0;
        void Add(ReadOnlySpan<byte> bytes)
        {
            hash.AppendData(bytes); length = checked(length + bytes.Length);
            if (prefix.Length < 32 * 1024) prefix.Write(bytes[..Math.Min(bytes.Length, 32 * 1024 - checked((int)prefix.Length))]);
        }
        Add(empty.AsSpan(0, start));
        if (value is null) Add("null"u8);
        else
        {
            Add("\""u8);
            for (var at = 0; at < value.Length;)
            {
                var take = Math.Min(512, value.Length - at);
                if (at + take < value.Length && char.IsHighSurrogate(value[at + take - 1])) take--;
                var encoded = JsonSerializer.SerializeToUtf8Bytes(value.Substring(at, take), EngineJson.Default.String);
                Add(encoded.AsSpan(1, encoded.Length - 2));
                at += take;
            }
            Add("\""u8);
        }
        Add(empty.AsSpan(end));
        using var shape = JsonDocument.Parse(empty);
        return new(shape.RootElement.GetProperty("kind").GetString()!, length <= 32 * 1024 ? Encoding.UTF8.GetString(prefix.ToArray()) : null,
            Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }
}
