using System.Text.Json;

namespace Assembly.Domain.Events;

/// <summary>The agent's slash commands (ACP <c>availableCommands</c>, raw). They go to the thread and the agent, not into the chat.</summary>
public sealed record CommandsChanged(long At, JsonElement Commands) : AgentEvent(At);
