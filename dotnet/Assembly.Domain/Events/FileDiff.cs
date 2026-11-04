namespace Assembly.Domain.Events;

/// <summary>Authoritative affected paths and the optional git patch a tool made or proposes.</summary>
public sealed record FileDiff(IReadOnlyList<string> Paths, string? Patch);
