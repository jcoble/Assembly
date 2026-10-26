namespace Assembly.Engine.Database;

/// <summary>Inside one write: its reads run on the write connection, so they see the write's own transaction.</summary>
public interface IWriteScope : IAppDatabase
{
    int Execute(string sql, params SqlArg[] args);
}
