using Assembly.Engine.Execution;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Retention;
using Assembly.Engine.Recording;
using EngineTopics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Engine;

/// <summary>Hosts dispose this owner before disposing the database.</summary>
public sealed class EngineRuntime : IAsyncDisposable, IEngineRuntime
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly IAppDatabase _db;
    private readonly TimeProvider _time;
    private readonly EngineTopics _topics;
    private readonly EngineTopics.Subscription _subscription;
    private readonly EngineTopics.Subscription _mirrorSubscription;
    private readonly Lock _mirrorsGate = new();
    private readonly Dictionary<string, (EnginePublisher Publisher, long Generation)> _mirrors = [];
    private readonly HashSet<string> _mirrorChanges = [];
    private readonly EngineWorker _worker;
    private readonly Task _publisherTask;
    private readonly Task _workerTask;
    private Task? _dispose;
    private readonly Lock _disposeGate = new();
    internal CancellationToken Stopping => _stop.Token;
    public string Machine { get; }
    public EngineCommands Commands { get; }
    public EngineReader Reader { get; }
    public UsageQueries Usage { get; }
    public EnginePublisher Publisher { get; }

    private EngineRuntime(IAppDatabase db, EngineTopics topics, TimeProvider time, SessionLimits limits,
        IEngineEffects? effects, IEngineRetention? retention, Func<int>? messageLimit)
    {
        _db = db;
        _time = time;
        _topics = topics;
        Machine = EngineSchema.Open(db, time.GetUtcNow().ToUnixTimeMilliseconds());
        Reader = new(db, Machine);
        Usage = new(db);
        Commands = new(Machine, limits, messageLimit);
        Publisher = new(db);
        _worker = new(db, Commands, Machine, time, effects, retention ?? new EngineRetention(
            () => Publisher.PublishedThrough, () => Publisher.MinimumReaderAck, Publisher.InvalidateReadersBefore));
        _subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => _wake.Writer.TryWrite(true));
        _mirrorSubscription = topics.Subscribe("__engine_mirrored", null, _ => { }, (machine, _) => MirrorChanged(machine!));
        _publisherTask = Task.Run(PublishAsync);
        _workerTask = Task.Run(() => _worker.RunAsync(_stop.Token));
        _wake.Writer.TryWrite(true); // catch up the durable log even without a new commit
    }

    public static EngineRuntime Start(IAppDatabase db, EngineTopics topics, TimeProvider time,
        SessionLimits limits, IEngineEffects? effects = null, IEngineRetention? retention = null, Func<int>? messageLimit = null) =>
        new(db, topics, time, limits, effects, retention, messageLimit);

    T IEngineRuntime.Write<T>(Func<IWriteScope, T> body)
    {
        var result = _db.Writer.Write("engine.command", body);
        _worker.Wake();
        return result;
    }

    public EnginePublisher PublisherFor(string machine)
    {
        if (machine == Machine) return Publisher;
        lock (_mirrorsGate)
        {
            var meta = MirrorMeta(machine);
            if (!_mirrors.TryGetValue(machine, out var current))
            {
                current = (new(_db, machine, meta.Through, () => MirrorChanged(machine), meta.Generation), meta.Generation);
                _mirrors.Add(machine, current);
            }
            return current.Publisher;
        }
    }

    private (long Through, long Generation) MirrorMeta(string machine) => _db.QueryFirst(
        "SELECT through,generation FROM engine_mirror WHERE machine=$machine AND state<>'retired'",
        r => ((long Through, long Generation)?)(r.Long(0), r.Long(1)), _stop.Token, new SqlArg("$machine", machine))
        ?? throw new KeyNotFoundException("Unknown mirror machine");

    private void MirrorChanged(string machine)
    {
        lock (_mirrorsGate) _mirrorChanges.Add(machine);
        _wake.Writer.TryWrite(true);
    }
    T IEngineRuntime.Read<T>(Func<IReadScope, T> body, CancellationToken cancellationToken)
    {
        using var snapshot = _db.OpenSnapshot(cancellationToken);
        return body(snapshot);
    }

    internal CommandResult Submit(CommandContext context, EngineCommand command) =>
        ((IEngineRuntime)this).Write(scope => Commands.Apply(scope, context, command));

    /// <summary>Reads the owning entity's version and epoch in the same writer as its fact.</summary>
    internal CommandResult SubmitCurrent(string kind, string entityId, EngineCommand command) =>
        ((IEngineRuntime)this).Write(scope =>
        {
            var sql = kind switch
            {
                "session" => "SELECT version,epoch FROM engine_session WHERE id=$id AND machine=$machine",
                "needs_me" => "SELECT n.version,s.epoch FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id WHERE n.id=$id AND s.machine=$machine",
                "tool_call" => "SELECT version,epoch FROM engine_tool_call WHERE id=$id AND machine=$machine",
                "usage" => "SELECT version,epoch FROM engine_usage WHERE id=$id AND machine=$machine",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var current = scope.QueryFirst(sql, r => ((long Version, long Epoch)?)(r.Long(0), r.Long(1)),
                new SqlArg("$id", entityId), new SqlArg("$machine", Machine));
            var sessionId = command switch
            {
                global::Assembly.Engine.NeedsMe.OpenNeed open => open.SessionId,
                ToolStarted start => start.Attribution.SessionId,
                TurnUsage usage => usage.Attribution.SessionId,
                _ => null
            };
            var creationEpoch = sessionId is null ? null : scope.QueryFirst(
                "SELECT epoch FROM engine_session WHERE id=$id AND machine=$machine",
                r => (long?)r.Long(0), new SqlArg("$id", sessionId), new SqlArg("$machine", Machine));
            if (current is null && creationEpoch is null)
                return new CommandResult(false, false, "bad_args", 0, 0, 0);
            var epoch = current?.Epoch ?? creationEpoch!.Value;
            return Commands.Apply(scope, new(Guid.NewGuid(), Machine, new(ActorKind.System, "adapter"),
                entityId, current?.Version ?? 0, epoch, _time.GetUtcNow().ToUnixTimeMilliseconds()), command);
        });

    public void EffectsAvailable() => _worker.EffectsAvailable();

    private async Task PublishAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stop.Token))
            {
                while (_wake.Reader.TryRead(out _)) { }
                Publisher.Drain(_stop.Token);
                string[] changed;
                lock (_mirrorsGate) { changed = _mirrorChanges.ToArray(); _mirrorChanges.Clear(); }
                foreach (var machine in changed)
                {
                    (long Through, long Generation) meta;
                    try { meta = MirrorMeta(machine); }
                    catch (KeyNotFoundException) { continue; } // A replaced remote may already be retired.
                    EnginePublisher? publisher = null;
                    bool reset = false;
                    lock (_mirrorsGate)
                    {
                        if (_mirrors.TryGetValue(machine, out var current))
                        {
                            publisher = current.Publisher;
                            reset = current.Generation != meta.Generation;
                            _mirrors[machine] = (publisher, meta.Generation);
                        }
                    }
                    // A newly opened publisher already represents this generation. Delayed notices must not
                    // reset its readers or release their acknowledgements a second time.
                    if (reset) publisher!.Reset(meta.Through, meta.Generation);
                    else publisher?.Drain(_stop.Token);
                    _db.Writer.Write("engine.mirror.retention", scope => EngineRetention.ApplyMirror(scope, machine,
                        publisher?.MinimumReaderAck ?? long.MaxValue));
                }
                _worker.Wake();
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
        _topics.Unsubscribe(_mirrorSubscription);
        await _stop.CancelAsync();
        try { await Task.WhenAll(_workerTask, _publisherTask); }
        finally { _stop.Dispose(); }
    }
}
