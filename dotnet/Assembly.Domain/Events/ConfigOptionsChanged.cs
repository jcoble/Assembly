using System.Text.Json;

namespace Assembly.Domain.Events;

/// <summary>The agent changed the session's config options or their values. They go to the session, not into the chat.</summary>
public sealed record ConfigOptionsChanged(long At, JsonElement Options) : AgentEvent(At);
