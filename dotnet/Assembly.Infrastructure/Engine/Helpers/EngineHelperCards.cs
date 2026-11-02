using System.Collections.Immutable;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Domain.Rows;

namespace Assembly.Infrastructure.Engine.Helpers;

/// <summary>Projects committed Engine children through the existing helper card writer.</summary>
public sealed partial class EngineHelperCards(IAppDatabase db, TimeProvider time, TextWriter log) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private ImmutableDictionary<string, Card> _current = ImmutableDictionary<string, Card>.Empty;
    private readonly Dictionary<string, (Card Card, long Order)> _pending = [];
    private readonly Dictionary<string, SubagentPayload> _shown = [];
    private Task? _running;
    private bool _started;
    private bool _stopped;
    private long _order;

    /// <summary>Start after Engine initialization and registry construction; one SQL read seeds the cache.</summary>
    public void Start(HelperRow row)
    {
        lock (_gate)
        {
            if (_started || _stopped) throw new InvalidOperationException("Helper cards already started or stopped.");
            _started = true;
        }
        // Read outside this lock: an active writer may be publishing a captured cache update.
        using var scope = db.OpenSnapshot(CancellationToken.None);
        foreach (var card in Read(scope)) Publish(card);
        lock (_gate) _running = Task.Run(() => RunAsync(row));
    }

    public bool Working(string parentId) => Volatile.Read(ref _current).Values.Any(card =>
        card.ParentId == parentId && card.Payload.State == "running");

    /// <summary>Capture in the caller's transaction; the callback only publishes memory after commit.</summary>
    public void Observe(IWriteScope scope, string childId)
    {
        foreach (var card in Read(scope, childId)) scope.AfterCommit(() => Publish(card));
    }

    /// <summary>Call after a mirror page establishes its current generation, in that same writer.</summary>
    public void Mirrored(IWriteScope scope, string machine)
    {
        foreach (var card in Read(scope, source: machine)) scope.AfterCommit(() => Publish(card));
    }

    /// <summary>Copied terminal rows supply display details; current Engine state still decides activity.</summary>
    public void Refresh(string childId)
    {
        using var scope = db.OpenSnapshot(CancellationToken.None);
        foreach (var card in Read(scope, childId)) Publish(card);
    }

    private void Publish(Card card)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_current.TryGetValue(card.Id, out var previous) && card.OlderThan(previous)) return;
            _current = _current.SetItem(card.Id, card);
            if (!_shown.ContainsKey(card.Id) && card.Previous is { } existing) _shown[card.Id] = existing;
            if (_shown.GetValueOrDefault(card.Id) == card.Payload && !_pending.ContainsKey(card.Id)) return;
            _pending[card.Id] = (card, ++_order);
            _wake.Writer.TryWrite(true);
        }
    }

    private async Task RunAsync(HelperRow row)
    {
        await foreach (var _ in _wake.Reader.ReadAllAsync())
        {
            Card[] pending;
            lock (_gate) { pending = _pending.Values.OrderBy(item => item.Order).Select(item => item.Card).ToArray(); _pending.Clear(); }
            foreach (var card in pending)
            {
                lock (_gate)
                    if (_current.GetValueOrDefault(card.Id)?.Payload != card.Payload ||
                        _shown.GetValueOrDefault(card.Id) == card.Payload) continue;
                try
                {
                    // This owns the ordinary card row write; never runs in a child writer or AfterCommit.
                    row.Show(card.Session, card.Payload.State, card.Payload.StopReason, card.Payload.Error);
                    lock (_gate) _shown[card.Id] = card.Payload;
                }
                catch (Exception error) { log.WriteLine($"{time.GetUtcNow():O} helper card {card.Id}: {error.Message}"); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate) { _stopped = true; _wake.Writer.TryComplete(); running = _running; }
        if (running is not null) await running;
    }
}
