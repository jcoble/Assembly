using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Socket;

/// <summary>Fixed opted-in legacy sessions framing; full stored and in-process titles remain exact.</summary>
internal static class SessionItemWireCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static ReadOnlyMemory<byte> Adapt(ReadOnlyMemory<byte> frame, string publisherEngineMachine)
    {
        if (string.IsNullOrWhiteSpace(publisherEngineMachine)) throw new CallFailed("session title publisher identity unavailable");
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        var state = root.GetProperty("t").GetString() switch
        {
            "state" => true,
            "change" => false,
            _ => throw new CallFailed("invalid sessions publication")
        };
        var subWidth = root.GetProperty("sub").GetRawText().Length;
        var maximumIdExtra = long.MinValue.ToString(CultureInfo.InvariantCulture).Length - subWidth;
        var selected = new Dictionary<int, byte[]>();
        if (!state && frame.Length + maximumIdExtra <= SocketConnection.MaxFrame) return frame;
        var complete = WriteFrame(root, state, selected);
        if (complete.Length + maximumIdExtra <= SocketConnection.MaxFrame) return complete;
        var items = state ? root.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("item")).ToArray()
            : [root.GetProperty("item")];
        var candidates = new List<(int Index, byte[] Notice, int Saving)>();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (!item.TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null) continue;
            if (title.ValueKind != JsonValueKind.String) throw new CallFailed("invalid sessions title");
            var bytes = Utf8.GetBytes(title.GetString()!);
            var reference = new LegacySessionTitleRef(item.GetProperty("sessionId").GetString()!, publisherEngineMachine,
                Nullable(item, "machine"), Nullable(item, "parentSessionId"), Nullable(item, "titleSource"),
                item.GetProperty("updatedAt").GetInt64(), bytes.LongLength, SessionTitleHash.Compute(bytes));
            var notice = WriteNotice(item, reference);
            var saving = Utf8.GetByteCount(item.GetRawText()) - notice.Length;
            if (saving > 0) candidates.Add((i, notice, saving));
        }
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Saving).ThenBy(candidate => candidate.Index))
        {
            selected.Add(candidate.Index, candidate.Notice);
            complete = WriteFrame(root, state, selected);
            if (complete.Length + maximumIdExtra <= SocketConnection.MaxFrame) return complete;
        }
        throw new CallFailed("sessions snapshot non-title metadata exceeds the existing frame limit");
    }

    private static string? Nullable(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.GetString() : null;

    private static byte[] WriteNotice(JsonElement item, LegacySessionTitleRef reference)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                if (property.NameEquals("title")) continue;
                json.WritePropertyName(property.Name); json.WriteRawValue(property.Value.GetRawText());
            }
            json.WriteNumber("sessionTitleRefVersion", 1);
            json.WritePropertyName("titleReference");
            JsonSerializer.Serialize(json, reference, SessionsJson.Default.LegacySessionTitleRef);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteFrame(JsonElement root, bool state, IReadOnlyDictionary<int, byte[]> selected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                json.WritePropertyName(property.Name);
                if (state && property.NameEquals("items"))
                {
                    json.WriteStartArray(); var index = 0;
                    foreach (var entry in property.Value.EnumerateArray())
                    {
                        if (!selected.TryGetValue(index++, out var notice)) { json.WriteRawValue(entry.GetRawText()); continue; }
                        json.WriteStartObject();
                        foreach (var field in entry.EnumerateObject())
                        {
                            json.WritePropertyName(field.Name);
                            if (field.NameEquals("item")) json.WriteRawValue(notice);
                            else json.WriteRawValue(field.Value.GetRawText());
                        }
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }
                else if (!state && property.NameEquals("item") && selected.TryGetValue(0, out var notice)) json.WriteRawValue(notice);
                else json.WriteRawValue(property.Value.GetRawText());
            }
            if (state) json.WriteNumber("sessionTitleParts", 1);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
