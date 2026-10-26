namespace Assembly.Domain.Events;

/// <summary>
/// Something that happened in a session, in our own terms. The ACP reader makes these and the row converter
/// turns them into rows, so nothing past the reader knows ACP. <c>At</c> is when it happened, in Unix milliseconds.
/// </summary>
public abstract record AgentEvent(long At);
