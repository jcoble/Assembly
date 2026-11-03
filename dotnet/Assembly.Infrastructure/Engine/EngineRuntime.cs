using Assembly.Engine.Workflows.Scheduling;
using Assembly.Engine.Execution;
using Assembly.Infrastructure.Engine.Auth;
using Assembly.Engine.Providers;
using Assembly.Engine.Messages;
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
    private readonly IEngineEffects? _effects;
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
        IEngineEffects? effects, IEngineRetention? retention, Func<int>? messageLimit, SessionCutover? cutover, Func<EngineRuntime, IEngineEffects>? effectsFactory, Func<IReadScope, int>? readExpiryMinutes, Task? effectsReady, Func<string, string, bool>? isConfiguredProvider = null, Action<IWriteScope, string>? effectCompleted = null,
        Func<EngineRuntime, Func<CancellationToken, Task<IReadOnlyList<PreparedWorkflowDispatch>>>>? workflowDispatchFactory = null)
    {
        if (effects is not null && effectsFactory is not null) throw new ArgumentException("Supply effects or effectsFactory, not both.");
        _db = db;
        _time = time;
        _topics = topics;
        Action<IWriteScope>? projectionSchema = cutover is null ? null : cutover.EnsureSchema;
        Action<IWriteScope, CommandContext, StartSession>? projectStart = cutover is null ? null : cutover.ProjectStart;
        Func<string, string>? accountContext = cutover is null ? null : cutover.AccountContext;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var expiryPolicy = new MessageExpiryPolicy(readExpiryMinutes);
        var initialized = db.Writer.Write("engine.initialize", scope =>
        {
            var schema = EngineSchema.Initialize(scope, now, projectionSchema, readExpiryMinutes?.Invoke(scope) ?? 30);
            var commands = new EngineCommands(schema.Machine, limits, messageLimit, projectStart, accountContext, ProjectCompletion, expiryPolicy, isConfiguredProvider, projectProjection: cutover?.ProjectProjection);
            cutover?.Initialize(scope, commands, schema.Machine, now, schema.PreviousVersion is 1 or 2 or 3);
            AuthInitialization.Interrupt(scope, commands, schema.Machine, now);
            return (Machine: schema.Machine, Commands: commands);
        });
        Machine = initialized.Machine;
        Commands = initialized.Commands;
        cutover?.Attach(Commands, Machine);
        Reader = new(db, Machine);
        Usage = new(db);
        Publisher = new(db);
        _effects = effectsFactory is null ? effects : effectsFactory(this);
        if (_effects is EffectRouter router) router.Seal();
        _worker = new(db, Commands, Machine, time, _effects, retention ?? new EngineRetention(
            () => Publisher.PublishedThrough, () => Publisher.MinimumReaderAck, Publisher.InvalidateReadersBefore), workflowDispatchFactory?.Invoke(this), effectCompleted);
        _subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => _wake.Writer.TryWrite(true));
        _mirrorSubscription = topics.Subscribe("__engine_mirrored", null, _ => { }, (machine, _) => MirrorChanged(machine!));
        _publisherTask = Task.Run(PublishAsync);
        _workerTask = Task.Run(() => RunWorkerAsync(effectsReady));
        _wake.Writer.TryWrite(true); // catch up the durable log even without a new commit
    }

    public static EngineRuntime Start(IAppDatabase db, EngineTopics topics, TimeProvider time,
        SessionLimits limits, IEngineEffects? effects = null, IEngineRetention? retention = null, Func<int>? messageLimit = null, SessionCutover? cutover = null,
        Func<EngineRuntime, IEngineEffects>? effectsFactory = null, Func<IReadScope, int>? readExpiryMinutes = null, Task? effectsReady = null, Func<string, string, bool>? isConfiguredProvider = null, Action<IWriteScope, string>? effectCompleted = null,
        Func<EngineRuntime, Func<CancellationToken, Task<IReadOnlyList<PreparedWorkflowDispatch>>>>? workflowDispatchFactory = null) =>
        new(db, topics, time, limits, effects, retention, messageLimit, cutover, effectsFactory, readExpiryMinutes, effectsReady, isConfiguredProvider, effectCompleted, workflowDispatchFactory);

    private async Task RunWorkerAsync(Task? ready)
    {
        try
        {
            if (ready is not null) await ready.WaitAsync(_stop.Token);
            _stop.Token.ThrowIfCancellationRequested();
            await _worker.RunAsync(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || ready?.IsCanceled == true) { }
    }

    private MessageOutcome? ProjectCompletion(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt, MessageDelivery? delivery)
    {
        if (_effects is EffectRouter router) return router.ProjectCompletion(scope, context, effect, receipt, delivery);
        if (_effects is AdapterEffects adapter && effect.Kind == "send")
            return adapter.ProjectCompletion(scope, context, effect, receipt, delivery ?? throw new InvalidOperationException("Missing validated delivery."));
        return null;
    }

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

    /// <summary>Refreshes the entity version while preserving the callback's original epoch.</summary>
    internal CommandResult SubmitCurrent(string kind, string entityId, EngineCommand command, long observedEpoch) =>
        ((IEngineRuntime)this).Write(scope => SubmitCurrent(scope, kind, entityId, command, observedEpoch));

    internal CommandResult SubmitCurrent(IWriteScope scope, string kind, string entityId, EngineCommand command, long observedEpoch)
    {
        var sql = kind switch
        {
            "session" => "SELECT version FROM engine_session WHERE id=$id AND machine=$machine AND state<>'foreign'",
            "needs_me" => "SELECT n.version FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id WHERE n.id=$id AND s.machine=$machine AND s.state<>'foreign'",
            "tool_call" => "SELECT version FROM engine_tool_call WHERE id=$id AND machine=$machine",
            "usage" => "SELECT version FROM engine_usage WHERE id=$id AND machine=$machine",
            "pr_observation" => "SELECT version FROM engine_pr_observation WHERE id=$id AND machine=$machine",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var version = scope.QueryFirst(sql, r => (long?)r.Long(0), new SqlArg("$id", entityId), new SqlArg("$machine", Machine));
        var parent = command switch
        {
            global::Assembly.Engine.NeedsMe.OpenNeed open when kind == "needs_me" => open.SessionId,
            ToolStarted start when kind == "tool_call" => start.Attribution.SessionId,
            TurnUsage usage when kind == "usage" => usage.Attribution.SessionId,
            global::Assembly.Engine.Prs.RecordPrObservation observation when kind == "pr_observation" => observation.Facts.SessionId,
            _ => null
        };
        if (version is null && (parent is null || scope.QueryFirst(
                "SELECT COUNT(*) FROM engine_session WHERE id=$id AND machine=$machine AND state<>'foreign'",
                r => r.Long(0), new SqlArg("$id", parent), new SqlArg("$machine", Machine)) != 1))
            return new(false, false, "bad_args", 0, 0, observedEpoch);
        return Commands.Apply(scope, new(Guid.NewGuid(), Machine, new(ActorKind.System, "adapter"),
            entityId, version ?? 0, observedEpoch, _time.GetUtcNow().ToUnixTimeMilliseconds()), command);
    }

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
        finally
        {
            try { if (_effects is EffectRouter router) await router.DisposeAsync(); }
            finally { _stop.Dispose(); }
        }
    }
}
