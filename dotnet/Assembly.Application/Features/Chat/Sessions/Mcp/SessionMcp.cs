using System.Text.Json;

namespace Assembly.Application.Features.Chat.Sessions.Mcp;

/// <summary>Host-owned endpoint readiness and callbacks for the actual provider open lifetime.</summary>
public sealed record SessionMcp(
    Func<CancellationToken, Task> WaitReady,
    Func<string, SessionObservation?, IReadOnlyList<JsonElement>> ServersFor,
    Action<string> Revoke,
    Action<string, SessionObservation?> ValidateOpen);
