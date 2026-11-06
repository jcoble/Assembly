using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Sessions;

internal static class MachineStartCanonical
{
    internal static StartCanonical FromPrepared(PreparedSessionTitleCommand prepared)
    {
        if (prepared.Command is not StartSession) throw new ArgumentException("Start required.");
        if (!prepared.Oversized) return new StartCanonicalSmall(prepared.CanonicalJournal);
        using var json = JsonDocument.Parse(prepared.CanonicalJournal); var root = json.RootElement;
        return new StartCanonicalLarge(root.GetProperty("commandSha256").GetString()!, root.GetProperty("commandUtf8Bytes").GetInt64(),
            root.GetProperty("titleUtf8Bytes").GetInt64());
    }
    internal static string Json(StartCanonical canonical)
    {
        if (canonical is StartCanonicalSmall small)
        {
            if (RelayHash.Utf8.GetByteCount(small.Canonical) > 32 * 1024 ||
                JsonSerializer.Deserialize(small.Canonical, EngineJson.Default.EngineCommand) is not StartSession start ||
                JsonSerializer.Serialize<EngineCommand>(start, EngineJson.Default.EngineCommand) != small.Canonical)
                throw new ArgumentException("bad_args");
            return small.Canonical;
        }
        if (canonical is not StartCanonicalLarge large || !RelayHash.IsSha(large.CommandSha256) || large.CommandUtf8Bytes <= 32768 ||
            large.TitleUtf8Bytes < 0 || large.CommandUtf8Bytes < large.TitleUtf8Bytes) throw new ArgumentException("bad_args");
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("kind", "start_session"); json.WriteNumber("titleJournalVersion", 1);
            json.WriteString("commandSha256", large.CommandSha256); json.WriteNumber("commandUtf8Bytes", large.CommandUtf8Bytes);
            json.WriteNumber("titleUtf8Bytes", large.TitleUtf8Bytes); json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
