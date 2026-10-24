using System.Text.Json.Serialization;

namespace Assembly.Engine.Acp.Messages;

/// <summary>
/// What we offer the agent: form questions (<c>elicitation/create</c>), subagents as child sessions (the subagents
/// RFD: <c>subagents: {}</c>), and nothing else of ACP's own: the agent
/// reads files and runs commands itself, and ACP v2 removes the <c>fs/*</c> and <c>terminal/*</c> client methods
/// anyway. <c>_meta</c> carries the JetBrains AIR extensions an agent's descriptor opts into, if any.
/// </summary>
public sealed record ClientCapabilities(
    FileSystemCapabilities Fs, bool Terminal, ElicitationCapabilities Elicitation, SubagentCapabilities Subagents,
    [property: JsonPropertyName("_meta")] ClientMeta? Meta = null)
{
    public static ClientCapabilities None { get; } =
        new(new FileSystemCapabilities(false, false), false, new ElicitationCapabilities(new FormElicitation()), new SubagentCapabilities());

    public static ClientCapabilities WithAir(IReadOnlyList<string>? air) =>
        air is { Count: > 0 } ? None with { Meta = new ClientMeta(new JetBrainsMeta(new AirMeta(1, air))) } : None;
}

/// <summary>Empty: declaring it is the opt-in.</summary>
public sealed record SubagentCapabilities;

public sealed record ClientMeta(JetBrainsMeta Jetbrains);

public sealed record JetBrainsMeta(AirMeta Air);

public sealed record AirMeta(int Version, IReadOnlyList<string> Capabilities);
