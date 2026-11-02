using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assembly.Engine.Providers;

public sealed class PreparedProviderCheck
{
    internal RecordProviderCheck Command { get; }
    internal byte[] FactsUtf8 { get; }
    internal string CanonicalJournal { get; }

    internal PreparedProviderCheck(RecordProviderCheck command)
    {
        ProviderFactsShape.Validate(command);
        Command = command with { Facts = command.Facts.Clone() };
        FactsUtf8 = new UTF8Encoding(false, true).GetBytes(Command.Facts.GetRawText());
        var canonical = new ProviderCheckCanonical("provider_check_v1", command.Installation, command.AuthState,
            command.Reason, command.Probe, FactsUtf8.LongLength, Convert.ToHexStringLower(SHA256.HashData(FactsUtf8)));
        CanonicalJournal = JsonSerializer.Serialize(canonical, EngineJson.Default.ProviderCheckCanonical);
    }
}

public sealed record ProviderCheckCanonical(string Kind, string Installation, string AuthState, string? Reason,
    ProviderProbeBinding Probe, long FactsUtf8Bytes, string FactsSha256);
