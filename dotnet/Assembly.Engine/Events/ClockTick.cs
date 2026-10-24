namespace Assembly.Engine.Events;

/// <summary>
/// Time passed with no new event. It lets text that was held back for coalescing go out
/// when the agent pauses, and it keeps the converter's output a function of its input.
/// </summary>
public sealed record ClockTick(long At) : AgentEvent(At);
