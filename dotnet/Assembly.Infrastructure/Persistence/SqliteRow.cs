using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;

namespace Assembly.Infrastructure.Persistence;

/// <summary>The reader's current row, read by column position.</summary>
internal sealed class SqliteRow(SqliteDataReader reader) : IRow
{
    public bool IsNull(int column) => reader.IsDBNull(column);

    public long Long(int column) => reader.GetInt64(column);

    public double Real(int column) => reader.GetDouble(column);

    public string Text(int column) => reader.GetString(column);

    public byte[] Blob(int column) => (byte[])reader.GetValue(column);
}
