using System.Text;

namespace Assembly.Tests;

/// <summary>
/// A log that background threads write while the test reads it. A plain StringWriter isn't safe for that and fails
/// now and then with "Destination is too short". Every other write ends in one of the three Writes below or in
/// WriteLine(string), each under the lock, so a line always lands whole. A test can wait for a line, with no polling.
/// </summary>
public sealed class SharedLog : TextWriter
{
    private readonly StringBuilder _text = new();
    private readonly List<(string Text, TaskCompletionSource Seen)> _waits = [];

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_text)
            _text.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_text)
            Wrote(new string(buffer, index, count));
    }

    public override void Write(string? value)
    {
        lock (_text)
            Wrote(value);
    }

    public override void WriteLine(string? value)
    {
        lock (_text)
            Wrote(value + NewLine);
    }

    /// <summary>Done once a write with <paramref name="text"/> has landed; fails after 10 s.</summary>
    public Task WrittenAsync(string text)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_text)
        {
            if (_text.ToString().Contains(text, StringComparison.Ordinal))
                seen.SetResult();
            else
                _waits.Add((text, seen));
        }
        return seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Wrote(string? value)
    {
        _text.Append(value);
        foreach (var wait in _waits.Where(wait => value?.Contains(wait.Text, StringComparison.Ordinal) == true))
            wait.Seen.TrySetResult();
    }

    public override string ToString()
    {
        lock (_text)
            return _text.ToString();
    }
}
