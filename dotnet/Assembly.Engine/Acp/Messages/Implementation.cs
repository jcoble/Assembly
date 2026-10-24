namespace Assembly.Engine.Acp.Messages;

/// <summary>Name and version of a client or agent (<c>clientInfo</c> / <c>agentInfo</c>).</summary>
public sealed record Implementation(string Name, string Version, string? Title = null);
