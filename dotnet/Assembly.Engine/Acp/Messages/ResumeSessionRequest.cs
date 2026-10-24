using System.Text.Json;

namespace Assembly.Engine.Acp.Messages;

/// <summary>Reopens a session in a new adapter process. The agent reloads its own transcript and replays nothing to us.</summary>
public sealed record ResumeSessionRequest(string SessionId, string Cwd, IReadOnlyList<JsonElement> McpServers);
