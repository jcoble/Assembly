namespace Assembly.Domain.Sessions;

/// <summary>
/// A session's config: the options its agent reported (raw JSON, null before its first session), the values
/// chosen, and the agent's slash commands (ACP <c>availableCommands</c>, raw JSON, null before it sent any).
/// </summary>
public sealed record SessionConfig(string? Options, IReadOnlyDictionary<string, string> Choices, string? Commands = null, string? ConfigTarget = null);

public sealed record InitialSessionConfig(string TargetId, IReadOnlyDictionary<string, string> Choices);

public sealed record ComposerCatalogue(SessionConfig Config, IReadOnlyDictionary<string, string> ModelOptions);
