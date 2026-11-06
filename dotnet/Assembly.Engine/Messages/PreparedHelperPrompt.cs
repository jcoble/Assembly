using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

/// <summary>Trusted host-derived ordinary message, never the raw Window owner command.</summary>
public sealed class PreparedHelperPrompt
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal SendMessage Command { get; }
    internal byte[] BodyUtf8 { get; }
    internal byte[] CallerTitleUtf8 { get; }
    internal long CallerEpoch { get; }
    internal bool Oversized { get; }
    internal string CanonicalJournal { get; }

    internal PreparedHelperPrompt(SendMessage command, byte[] bodyUtf8, byte[] callerTitleUtf8,
        long callerEpoch, bool oversized, string canonicalJournal)
    {
        Command = command; BodyUtf8 = bodyUtf8; CallerTitleUtf8 = callerTitleUtf8;
        CallerEpoch = callerEpoch; Oversized = oversized; CanonicalJournal = canonicalJournal;
    }

    public static PreparedHelperPrompt Prepare(SendMessage command, string actualCallerTitle, long actualCallerEpoch)
    {
        if (command.FromSessionId is null || actualCallerEpoch < 0) throw new ArgumentException("Invalid helper caller.");
        var body = Utf8.GetBytes(command.Text);
        var title = Utf8.GetBytes(actualCallerTitle);
        var canonical = PreparedTextJson.Create(command with { Text = "" }, "text", command.Text);
        if (canonical.SmallCanonical is { } small) return new(command, body, title, actualCallerEpoch, false, small);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("kind", "send_message"); json.WriteNumber("helperJournalVersion", 1);
            json.WriteString("commandSha256", canonical.Sha256); json.WriteNumber("commandUtf8Bytes", canonical.Utf8Bytes);
            json.WriteString("callerTitleSha256", Convert.ToHexStringLower(SHA256.HashData(title)));
            json.WriteNumber("callerTitleUtf8Bytes", title.LongLength); json.WriteNumber("callerEpoch", actualCallerEpoch);
            json.WriteEndObject();
        }
        return new(command, body, title, actualCallerEpoch, true, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
