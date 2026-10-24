using Assembly.Engine.Rows.Payloads;

namespace Assembly.Engine.Events;

/// <summary>A subagent's row in its parent's thread, as it stands now.</summary>
public sealed record SubagentShown(long At, string SessionId, SubagentPayload Subagent) : AgentEvent(At);
