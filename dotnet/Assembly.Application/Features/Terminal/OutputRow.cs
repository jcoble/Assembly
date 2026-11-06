using System.Text.Json.Serialization;

namespace Assembly.Application.Features.Terminal;

[JsonConverter(typeof(JsonStringEnumConverter<TerminalUnavailable>))]
public enum TerminalUnavailable
{
    [JsonStringEnumMemberName("connection_lost")] ConnectionLost,
    [JsonStringEnumMemberName("terminal_missing")] TerminalMissing,
    [JsonStringEnumMemberName("source_changed")] SourceChanged
}

/// <summary>Byte/exit offsets keep their normal meaning; availability carries no invented offset or exit.</summary>
public sealed record OutputRow(long? Seq, long? PrevSeq, byte[]? Data = null, int? Exit = null, TerminalUnavailable? Unavailable = null);
