namespace Assembly.Domain.Sessions;

/// <summary>Actual private discovery metadata and complete provider-returned options for each available model.</summary>
public sealed record ComposerCatalogue(SessionConfig Config, IReadOnlyDictionary<string, string> ModelOptions);
