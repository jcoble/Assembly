namespace Assembly.Application.Common.Database;

/// <summary>One result row, read by column position. So every query names its columns: never SELECT *.</summary>
public interface IRow
{
    bool IsNull(int column);

    long Long(int column);

    double Real(int column);

    string Text(int column);

    byte[] Blob(int column);
}
