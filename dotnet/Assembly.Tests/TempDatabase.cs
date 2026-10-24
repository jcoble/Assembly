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
