namespace Assembly.Application.Common.Database;

/// <summary>
/// Reads, in plain SQL, mapped by a function the caller writes (no reflection, so it's safe under Native AOT). The
/// token stops a read that's running. There are no write methods: every write goes through <see cref="IAtomicWriter"/>.
/// </summary>
public interface IAppDatabase
{
    /// <summary>The file's one writer: every write to it goes through this.</summary>
    IAtomicWriter Writer { get; }

    IReadSnapshot OpenSnapshot(CancellationToken cancellationToken);

    List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args);

    /// <summary>The first row mapped, or default when there's none.</summary>
    T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args);
}
