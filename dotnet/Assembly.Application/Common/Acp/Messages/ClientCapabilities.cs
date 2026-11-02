namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// ACP v2 client capabilities. The adapter owns file access and tool execution.
/// </summary>
public sealed record ClientCapabilities(ElicitationCapabilities Elicitation, AuthCapabilities Auth)
{
    public static ClientCapabilities Assembly { get; } = new(new(new(), new()), new(new()));
}

public sealed record AuthCapabilities(TerminalAuth Terminal);
public sealed record TerminalAuth;
