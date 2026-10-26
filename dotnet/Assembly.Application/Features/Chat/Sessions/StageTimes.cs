using Assembly.Application.Common.Agents;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// How long a turn took to reach each stage of its start, in ms since the send: the clone, the adapter start,
/// initialize, the session opened, the first update and the prompt's answer. Each stage counts once.
/// </summary>
public sealed class StageTimes(TimeProvider time)
{
    private readonly long _sent = time.GetTimestamp();
    private readonly List<(string Stage, long Ms)> _reached = [];

    public void Mark(string stage)
    {
        var ms = (long)time.GetElapsedTime(_sent).TotalMilliseconds;
        lock (_reached)
        {
            if (!_reached.Exists(reached => reached.Stage == stage))
                _reached.Add((stage, ms));
        }
    }

    /// <summary>The stages reached, in order, as <c>clone 812 ms, adapter 815 ms, …</c>.</summary>
    public override string ToString()
    {
        lock (_reached)
            return string.Join(", ", _reached.Select(reached => $"{reached.Stage} {reached.Ms} ms"));
    }
}
