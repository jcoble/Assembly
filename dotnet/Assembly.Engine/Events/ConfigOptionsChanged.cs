using System.Text.Json;

namespace Assembly.Engine.Events;

/// <summary>The agent changed the session's config options or their values. They go to the thread, not into the chat.</summary>
public sealed record ConfigOptionsChanged(long At, JsonElement Options) : AgentEvent(At);
