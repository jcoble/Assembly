namespace Assembly.Domain.Events;

/// <summary>The agent named the session. It goes to the sessions list, not into the chat.</summary>
public sealed record TitleChanged(long At, string Title) : AgentEvent(At);
