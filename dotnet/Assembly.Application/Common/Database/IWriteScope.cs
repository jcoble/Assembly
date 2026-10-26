namespace Assembly.Application.Common.Database;

/// <summary>
/// Inside one write: its reads run on the write connection, so they see the write's own transaction. They take no
/// token, because a save that has been sent finishes; cancelling one of them would roll back the whole write.
/// </summary>
public interface IWriteScope
{
    List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args);

    /// <summary>The first row mapped, or default when there's none.</summary>
    T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args);

    int Execute(string sql, params SqlArg[] args);
}
