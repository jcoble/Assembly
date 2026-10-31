using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Reading;
using EngineTopics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Engine;

/// <summary>Hosts dispose this owner before disposing the database.</summary>
public sealed class EngineRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly EngineTopics _topics;
    private readonly EngineTopics.Subscription _subscription;
    private readonly EngineWorker _worker;
    private readonly Task _publisherTask;
    private readonly Task _workerTask;
    private Task? _dispose;
    private readonly Lock _disposeGate = new();
    public string Machine { get; }
    public EngineCommands Commands { get; }
    public EngineReader Reader { get; }
    public EnginePublisher Publisher { get; }

    private EngineRuntime(IAppDatabase db, EngineTopics topics, TimeProvider time, Func<int> childLimit,
        IEngineEffects? effects, IEngineRetention retention)
    {
        _topics = topics;
        Machine = EngineSchema.Open(db, time.GetUtcNow().ToUnixTimeMilliseconds());
        Reader = new(db, Machine);
        Commands = new(Machine, childLimit);
        Publisher = new(db);
        _worker = new(db, Commands, Machine, time, effects, retention);
        _subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => _wake.Writer.TryWrite(true));
        _publisherTask = Task.Run(PublishAsync);
        _workerTask = Task.Run(() => _worker.RunAsync(_stop.Token));
        _wake.Writer.TryWrite(true); // catch up the durable log even without a new commit
    }

    public static EngineRuntime Start(IAppDatabase db, EngineTopics topics, TimeProvider time,
        Func<int> childLimit, IEngineEffects? effects = null, IEngineRetention? retention = null) =>
        new(db, topics, time, childLimit, effects, retention ?? new NoRetention());

    public void EffectsAvailable() => _worker.EffectsAvailable();

    private async Task PublishAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stop.Token))
            {
                while (_wake.Reader.TryRead(out _)) { }
                _worker.Wake();
                Publisher.Drain(_stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_dispose ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _topics.Unsubscribe(_subscription);
        await _stop.CancelAsync();
        try { await Task.WhenAll(_workerTask, _publisherTask); }
        finally { _stop.Dispose(); }
    }
}
