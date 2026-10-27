using System.Text;

namespace Assembly.Tests;

/// <summary>
/// A log that background threads write while the test reads it. A plain StringWriter isn't safe for that and fails
/// now and then with "Destination is too short". Every other write ends in one of the three Writes below or in
/// WriteLine(string), each under the lock, so a line always lands whole.
/// </summary>
public sealed class SharedLog : TextWriter
{
    private readonly StringBuilder _text = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_text)
            _text.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_text)
            _text.Append(buffer, index, count);
    }

    public override void Write(string? value)
    {
        lock (_text)
            _text.Append(value);
    }

    public override void WriteLine(string? value)
    {
        lock (_text)
            _text.Append(value).Append(CoreNewLine);
    }

    public override string ToString()
    {
        lock (_text)
            return _text.ToString();
    }
}
