using System.Text;

namespace Assembly.Tests;

/// <summary>
/// A log that background threads write while the test reads it. A plain StringWriter isn't safe for that and fails
/// now and then with "Destination is too short"; every other write ends in one of these three.
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

    public override string ToString()
    {
        lock (_text)
            return _text.ToString();
    }
}
