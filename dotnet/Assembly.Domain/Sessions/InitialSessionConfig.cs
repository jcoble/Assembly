namespace Assembly.Domain.Sessions;

/// <summary>The selected owning configuration and raw provider choices, saved together before first open.</summary>
public sealed record InitialSessionConfig(string TargetId, IReadOnlyDictionary<string, string> Choices);
