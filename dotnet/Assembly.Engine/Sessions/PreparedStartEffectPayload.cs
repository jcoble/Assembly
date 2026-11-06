using System.Text.Json;

namespace Assembly.Engine.Sessions;

/// <summary>Only the exact frozen Start command can consume this already-generated typed effect.</summary>
internal sealed class PreparedStartEffectPayload
{
    private readonly StartSession _command;
    private readonly JsonElement _payload;
    private PreparedStartEffectPayload(StartSession command)
    {
        _command = command;
        _payload = JsonSerializer.SerializeToElement(SessionStartEffect.From(command), EngineJson.Default.SessionStartEffect);
    }
    internal static PreparedStartEffectPayload Prepare(StartSession frozen) => new(frozen);
    internal JsonElement ForExact(StartSession frozen) => ReferenceEquals(_command, frozen)
        ? _payload : throw new InvalidOperationException("Prepared Start payload binding mismatch.");
}
