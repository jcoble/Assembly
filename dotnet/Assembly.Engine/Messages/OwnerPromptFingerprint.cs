using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Messages;

/// <summary>Binds exact original owner content without serializing large text into a journal command.</summary>
public static class OwnerPromptFingerprint
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static PreparedOwnerPrompt Prepare(string machine, SendOwnerPrompt request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AttachmentIds);
        var references = Array.AsReadOnly(request.AttachmentIds.ToArray());
        var snapshot = request with { AttachmentIds = references };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("engine-owner-prompt-content/v1\0"u8);
        AddString(hash, machine, cancellationToken);
        AddString(hash, snapshot.ToSessionId, cancellationToken);
        AddString(hash, snapshot.ClientId, cancellationToken);
        if (snapshot.TargetEpoch is { } epoch)
        {
            hash.AppendData([1]);
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(value, epoch);
            hash.AppendData(value);
        }
        else hash.AppendData([0]);
        var textBytes = AddString(hash, snapshot.Text, cancellationToken);
        AddLength(hash, references.Count);
        foreach (var reference in references) AddString(hash, reference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var attachmentJson = JsonSerializer.Serialize<IReadOnlyList<string>>(references, EngineJson.Default.IReadOnlyListString);
        var attachmentJsonBytes = Encode(attachmentJson, null, cancellationToken);
        var storageTextUtf8 = Utf8.GetBytes(snapshot.Text);
        cancellationToken.ThrowIfCancellationRequested();
        return new(machine, snapshot, attachmentJson, Convert.ToHexStringLower(hash.GetHashAndReset()), textBytes, attachmentJsonBytes, storageTextUtf8);
    }

    private static long AddString(IncrementalHash hash, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = Encode(text, null, cancellationToken);
        AddLength(hash, length);
        Encode(text, hash, cancellationToken);
        return length;
    }

    private static void AddLength(IncrementalHash hash, long length)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, checked((ulong)length));
        hash.AppendData(bytes);
    }

    private static long Encode(string text, IncrementalHash? hash, CancellationToken cancellationToken)
    {
        var encoder = Utf8.GetEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            var offset = 0;
            while (offset < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(4096, text.Length - offset);
                encoder.Convert(text.AsSpan(offset, count), buffer, offset + count == text.Length,
                    out var consumed, out var written, out _);
                hash?.AppendData(buffer.AsSpan(0, written));
                total = checked(total + written);
                offset += consumed;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
