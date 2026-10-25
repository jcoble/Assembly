namespace Assembly.Engine.Features;

/// <summary>What a feature gets from the host that serves it: the SQLite file it keeps its own tables in.</summary>
public sealed record FeatureContext(string Database);
