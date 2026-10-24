using System.Text.Json;
using Assembly.Engine.Rows.Payloads;

namespace Assembly.Engine.Events;

/// <summary>
/// The agent asked the user something (a permission, or a question with a form), or the ask was settled: answered,
/// or cancelled by a stop or the agent going. Each change replaces the ask's card.
/// </summary>
public sealed record AskChanged(long At, string AskId, AskPayload Ask) : AgentEvent(At);
