using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Remote.Smoke;

/// <summary>Reads the client's update queue and files each update under the current turn.</summary>
public sealed class UpdateWatcher
{
    private readonly ChannelReader<SessionNotification> _updates;
    private TurnLog _current = new();

    public UpdateWatcher(ChannelReader<SessionNotification> updates)
    {
        _updates = updates;
        Completion = Task.Run(WatchAsync);
    }

    public Task Completion { get; }

    public TurnLog BeginTurn()
    {
        var turn = new TurnLog();
        Volatile.Write(ref _current, turn);
        return turn;
    }

    /// <summary>The prompt answer can overtake the last updates in the queue; wait for the queue to empty.</summary>
    public async Task DrainAsync()
    {
        for (var i = 0; i < 100 && _updates.Count > 0; i++)
            await Task.Delay(10);
        await Task.Delay(20);
    }

    private async Task WatchAsync()
    {
        await foreach (var update in _updates.ReadAllAsync())
            Volatile.Read(ref _current).Add(update);
    }
}
