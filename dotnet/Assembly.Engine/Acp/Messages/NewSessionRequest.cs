using System.Text.Json;

namespace Assembly.Engine.Acp.Messages;

/// <summary><c>cwd</c> must be absolute. <c>mcpServers</c> is required by the schema, even when empty.</summary>
public sealed record NewSessionRequest(string Cwd, IReadOnlyList<JsonElement> McpServers);
