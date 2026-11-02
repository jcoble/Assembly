namespace Assembly.Engine.Mcp;

/// <summary>The engine session and epoch identified by an MCP bearer token.</summary>
public sealed record McpCaller(string SessionId, long Epoch);
