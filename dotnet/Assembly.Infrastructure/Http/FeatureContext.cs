using Assembly.Infrastructure.Persistence;

namespace Assembly.Infrastructure.Http;

/// <summary>What a feature gets from the host that serves it: the SQLite file it keeps its own tables in.</summary>
public sealed record FeatureContext(AppDatabase Database);
