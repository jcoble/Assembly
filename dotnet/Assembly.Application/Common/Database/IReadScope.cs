namespace Assembly.Application.Common.Database;

/// <summary>Reads on the caller's current transaction.</summary>
public interface IReadScope
{
    List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args);
    T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args);
}
