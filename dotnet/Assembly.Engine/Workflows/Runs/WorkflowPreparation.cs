using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Workflows.Runs;

internal static class WorkflowPreparation
{
    internal static PreparedWorkflowCommand Freeze(WorkflowStartBody body, CancellationToken ct)
    {
        var value = JsonSerializer.SerializeToElement(body, WorkflowPreparationJson.Default.WorkflowStartBody);
        var (json, sha, bytes) = Digest(value, ct);
        var frozen = JsonSerializer.Deserialize(json, WorkflowPreparationJson.Default.WorkflowStartBody)!;
        return new(frozen, json, sha, bytes);
    }

    internal static PreparedWorkflowCommand Freeze(Workspaces.WorkspaceBody body, CancellationToken ct)
    {
        var value = JsonSerializer.SerializeToElement(body, Workspaces.WorkspacePreparationJson.Default.WorkspaceBody);
        var (json, sha, bytes) = Digest(value, ct);
        var frozen = JsonSerializer.Deserialize(json, Workspaces.WorkspacePreparationJson.Default.WorkspaceBody)!;
        return new(frozen, json, sha, bytes);
    }

    private static (string Json, string Sha, long Bytes) Digest(JsonElement value, CancellationToken ct)
    {
        var json = Canonical(value);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes("workflow-prepared/v1"), ct);
        HashValue(hash, value, ct);
        return (json, Convert.ToHexStringLower(hash.GetHashAndReset()), Encoding.UTF8.GetByteCount(json));
    }

    internal static string RequestFingerprint(WorkflowStartRequest request, string projectId, string root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(request.OwnerSessionId); writer.WriteStringValue(projectId); writer.WriteStringValue(root);
            writer.WriteStringValue(request.Path); writer.WriteStringValue(request.Request);
            if (request.Task is { } task) Write(writer, task); else writer.WriteNullValue();
            Write(writer, request.Inputs); writer.WriteEndArray();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes("workflow-request/v1"), default);
        Append(hash, stream.ToArray(), default);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); Write(writer, item.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void HashValue(IncrementalHash hash, JsonElement value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        hash.AppendData([(byte)value.ValueKind]);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { Append(hash, Encoding.UTF8.GetBytes(item.Name), ct); HashValue(hash, item.Value, ct); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) HashValue(hash, item, ct);
        else if (value.ValueKind == JsonValueKind.String) Append(hash, Encoding.UTF8.GetBytes(value.GetString()!), ct);
        else if (value.ValueKind == JsonValueKind.Number) Append(hash, Encoding.UTF8.GetBytes(value.GetRawText()), ct);
        hash.AppendData([0xff]);
    }

    private static void Append(IncrementalHash hash, byte[] value, CancellationToken ct)
    {
        Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)value.LongLength);
        hash.AppendData(length);
        for (var offset = 0; offset < value.Length; offset += Math.Min(4096, value.Length - offset))
        { ct.ThrowIfCancellationRequested(); hash.AppendData(value.AsSpan(offset, Math.Min(4096, value.Length - offset))); }
    }
}
