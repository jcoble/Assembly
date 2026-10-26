namespace Assembly.Domain.Rows;

/// <summary>How full the agent's context window is, in tokens, and how big it is, as its latest usage report said.</summary>
public sealed record ContextPayload(long Used, long Size);
