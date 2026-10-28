namespace Assembly.Tests;

/// <summary>
/// A number background threads change while the test waits for it to reach a value. Each change, under the lock,
/// completes the waits for the value it lands on, so a test can wait for a value with no polling.
/// </summary>
public sealed class Count
{
    private readonly List<(int Value, TaskCompletionSource Reached)> _waits = [];
    private int _value;

    public int Value
    {
        get { lock (_waits) return _value; }
    }

    /// <summary>Adds <paramref name="by"/> and returns the new value.</summary>
    public int Add(int by = 1)
    {
        lock (_waits)
        {
            _value += by;
            foreach (var wait in _waits.Where(wait => wait.Value == _value))
                wait.Reached.TrySetResult();
            return _value;
        }
    }

    /// <summary>
    /// Done once the count is <paramref name="value"/>, now or after a change; fails after 10 s. The count may have moved
    /// on by the time the wait returns, so a test that means "exactly" asserts <see cref="Value"/> after it.
    /// </summary>
    public Task ReachedAsync(int value)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waits)
        {
            if (_value == value)
                reached.SetResult();
            else
                _waits.Add((value, reached));
        }
        return reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
