namespace Assembly.Application.Common.Database;

/// <summary>A read-only transaction. Its first read fixes the view until disposal.</summary>
public interface IReadSnapshot : IDisposable
{
    List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args);
    T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args);
}
