using System.Buffers;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

/// <summary>Frozen actual title command, using the original small canonical bytes whenever they fit.</summary>
public sealed class PreparedSessionTitleCommand
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal SessionCommand Command { get; }
    internal byte[]? TitleUtf8 { get; }
    internal string CanonicalJournal { get; }
    internal bool Oversized { get; }
    internal PreparedStartEffectPayload? StartEffectPayload { get; }

    private PreparedSessionTitleCommand(SessionCommand command, byte[]? titleUtf8, string canonicalJournal, bool oversized)
    {
        Command = command; TitleUtf8 = titleUtf8; CanonicalJournal = canonicalJournal; Oversized = oversized;
        StartEffectPayload = command is StartSession start ? PreparedStartEffectPayload.Prepare(start) : null;
    }

    internal static PreparedSessionTitleCommand Prepare(SessionCommand command, ReadOnlyMemory<byte>? exactTitleUtf8 = null)
    {
        command = command with { };
        var (title, empty) = command switch
        {
            ReportSessionTitle report => (report.Title, (SessionCommand)(report with { Title = "" })),
            RegisterForeignSession foreign => (foreign.Title, (SessionCommand)(foreign with { Title = "" })),
            StartSession start => (start.Title, (SessionCommand)(start with { Title = "" })),
            AdoptSession adopt => (adopt.Title, (SessionCommand)(adopt with { Title = "" })),
            _ => throw new ArgumentException("Unsupported title command.", nameof(command))
        };
        var bytes = title is null ? null : exactTitleUtf8?.ToArray() ?? Utf8.GetBytes(title);
        var canonical = PreparedTextJson.Create(empty, "title", title);
        if (canonical.SmallCanonical is { } small) return new(command, bytes, small, false);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("kind", canonical.Kind); json.WriteNumber("titleJournalVersion", 1);
            json.WriteString("commandSha256", canonical.Sha256); json.WriteNumber("commandUtf8Bytes", canonical.Utf8Bytes);
            json.WriteNumber("titleUtf8Bytes", bytes!.LongLength); json.WriteEndObject();
        }
        return new(command, bytes, Encoding.UTF8.GetString(buffer.WrittenSpan), true);
    }
}
