using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests;

/// <summary>A database path in the temp folder, deleted with its WAL files afterwards.</summary>
internal sealed class TempDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assembly-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
            File.Delete(file);
    }
}

/// <summary>A database for a test that doesn't look at the writer's clock or its publishes.</summary>
internal static class TestDatabase
{
    public static AppDatabase Open(string path) => AppDatabase.Open(path, TimeProvider.System, new global::Assembly.Infrastructure.Topics.Topics());
}
