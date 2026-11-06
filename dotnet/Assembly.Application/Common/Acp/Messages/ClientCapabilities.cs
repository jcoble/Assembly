using System.Text.Json.Serialization;

namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// ACP v2 client capabilities. The adapter owns file access and tool execution.
/// </summary>
public sealed record ClientCapabilities(ElicitationCapabilities Elicitation, AuthCapabilities Auth)
{
    public static ClientCapabilities Assembly { get; } = new(new(new(), new()), new(new()));

    public static ClientCapabilities WithTerminalAuthentication(bool enabled) =>
        new(Assembly.Elicitation, new(enabled ? new TerminalAuth() : null));
}

public sealed record AuthCapabilities(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerminalAuth? Terminal);
public sealed record TerminalAuth;
