using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Updates;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Desktop.Settings;

/// <summary>
/// Owns "make this machine match the switched-on providers" for the local machine.
/// Runs on app start, after an update, and on "Check again" (<c>settings/reconcile</c>). No timers or polling.
/// Concurrent triggers coalesce into a single run; every run is owned by this instance and cancelled on shutdown.
/// </summary>
internal sealed class LocalProviderReconciler : IFeature, IAsyncDisposable
{
    internal const string FeatureName = "settings";
    internal const string ReconcileMethod = "reconcile";
    internal const string Topic = "__settings_reconcile";
    private const string CurrentKey = "current";
    private const string LocalKey = "local";

    private readonly IAppDatabase _db;
    private readonly TopicSet _topics;
    private readonly BackgroundWork _work;
    private readonly IAgentLauncher _launcher;
    private readonly MachineUpdates _updates;
    private readonly ProviderEnablementStore _enablement;
    private readonly EngineRuntime _engine;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly Func<string, bool>? _detectLocalCli;
    private readonly bool _probeAfterCheck;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _inFlight;
    private bool _working;
    private bool _autoFailed;
    private bool _disposed;

    internal LocalProviderReconciler(
        IAppDatabase db,
        TopicSet topics,
        BackgroundWork work,
        IAgentLauncher launcher,
        MachineUpdates updates,
        ProviderEnablementStore enablement,
        EngineRuntime engine,
        TimeProvider time,
        TextWriter log,
        Func<string, bool>? detectLocalCli = null,
        bool probeAfterCheck = true)
    {
        _db = db;
        _topics = topics;
        _work = work;
        _launcher = launcher;
        _updates = updates;
        _enablement = enablement;
        _engine = engine;
        _time = time;
        _log = log;
        _detectLocalCli = detectLocalCli;
        _probeAfterCheck = probeAfterCheck;
    }

    /// <summary>True while a reconciliation run is actively working on this machine.</summary>
    internal bool IsWorking => Volatile.Read(ref _working);

    /// <summary>Raised when a concurrent trigger joins an in-flight reconciliation run.</summary>
    internal event Action? Coalesced;

    public void Register(ICallRegistry calls) =>
        calls.Read(FeatureName, ReconcileMethod, SettingsReconcileJson.Default.ReconcileArgs,
            SettingsReconcileJson.Default.ReconcileResult, HandleReconcileAsync);

    private async Task<ReconcileResult> HandleReconcileAsync(ReconcileArgs args, CallContext call)
    {
        if (call.Peer is not OwnerPeer)
            throw new CallFailed("Owner access is required.", "forbidden");
        if (!string.IsNullOrEmpty(args.Machine) && !string.Equals(args.Machine, LocalKey, StringComparison.Ordinal))
            throw new CallFailed("Only this machine can be reconciled right now.", "unsupported_machine");

        var started = await ReconcileAsync(fresh: true, call.Cancellation);
        return new ReconcileResult(started);
    }

    /// <summary>Starts a background reconciliation run if none is already in flight; concurrent triggers coalesce.</summary>
    internal void Trigger(bool fresh = false)
    {
        lock (_gate)
        {
            if (_disposed || _inFlight is not null)
                return;
            StartLocked(fresh);
        }
    }

    /// <summary>
    /// Runs or joins the single local reconciliation run. Returns true when this call started the run and false when
    /// it coalesced into an already-running reconciliation.
    /// </summary>
    internal async Task<bool> ReconcileAsync(bool fresh, CancellationToken ct)
    {
        Task run;
        bool started;
        lock (_gate)
        {
            if (_disposed)
                throw new OperationCanceledException(ct);
            if (_inFlight is { } running)
            {
                run = running;
                started = false;
            }
            else
            {
                run = StartLocked(fresh);
                started = true;
            }
        }
        if (!started)
            Coalesced?.Invoke();
        await run.WaitAsync(ct);
        return started;
    }

    private Task StartLocked(bool fresh)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight = done.Task;
        Volatile.Write(ref _working, true);
        _work.Start("local provider reconcile", stop => ExecuteAsync(fresh, done, stop));
        return done.Task;
    }

    private async Task ExecuteAsync(bool fresh, TaskCompletionSource done, CancellationToken hostStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStop, _stop.Token);
        var ct = linked.Token;
        PublishWorking(true);
        try
        {
            // Yield so callers that triggered under another component's lock never re-enter synchronously.
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            var enabledSpecs = SeedAndReadEnabled(ct);
            var localItems = await _updates.CheckLocalAsync(ct, fresh);
            if (enabledSpecs.Length == 0)
            {
                if (fresh) _updates.ClearLocalFailureIfIdle();
                return;
            }

            var (toolsBehind, adaptersNewer, repair) = SelectToolsToUpdate(localItems, enabledSpecs);
            if (toolsBehind.Count == 0)
            {
                if (fresh)
                    _updates.ClearLocalFailureIfIdle();
                // Even with nothing to update, sign-in is checked in this run (also at startup).
                TriggerLocalProbes(enabledSpecs, ct);
                return;
            }

            if (fresh)
                _autoFailed = false;
            else if (_autoFailed)
                return;

            var adaptersWereBehind = toolsBehind.Any(item => item.Kind == "adapter");
            var updated = await _updates.UpdateLocalToolsAsync(toolsBehind, adaptersNewer, repair, ct);
            if (!updated)
            {
                if (!fresh) _autoFailed = true;
                return;
            }

            _autoFailed = false;
            localItems = await _updates.CheckLocalAsync(ct, fresh: false);

            // When a fresh profile just installed its adapters, agents.json now lists its CLIs for the first time:
            // run at most one CLI update pass if any enabled CLI is behind, then recheck once.
            if (adaptersWereBehind)
            {
                var clisBehind = SelectBehindClis(localItems, enabledSpecs);
                if (clisBehind.Count > 0)
                {
                    var cliUpdated = await _updates.UpdateLocalToolsAsync(clisBehind, adaptersNewer: false, repair: false, ct);
                    if (cliUpdated)
                        await _updates.CheckLocalAsync(ct, fresh: false);
                    else if (!fresh)
                        _autoFailed = true;
                }
            }

            TriggerLocalProbes(enabledSpecs, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or JsonException)
        {
            _log.WriteLine($"local provider reconcile: {error.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
                Volatile.Write(ref _working, false);
            }
            PublishWorking(false);
            done.TrySetResult();
        }
    }

    private ProviderSpec[] SeedAndReadEnabled(CancellationToken ct)
    {
        var knownIds = ProviderCatalog.Known.Select(spec => spec.Id).ToHashSet(StringComparer.Ordinal);
        var detected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var agent in _launcher.Agents)
            if (knownIds.Contains(agent.Id))
                detected.Add(agent.Id);

        Dictionary<string, bool> saved;
        using (var snapshot = _db.OpenSnapshot(ct))
        {
            saved = new Dictionary<string, bool>(ProviderEnablementStore.ReadAll(snapshot), StringComparer.Ordinal);
            var engineInstalled = snapshot.Query(
                "SELECT provider FROM engine_provider WHERE machine = $machine AND installation = 'installed';",
                row => row.Text(0),
                new SqlArg("$machine", _engine.Machine));
            foreach (var provider in engineInstalled)
                if (knownIds.Contains(provider))
                    detected.Add(provider);
        }

        if (_detectLocalCli is not null)
        {
            foreach (var spec in ProviderCatalog.Known)
            {
                if (saved.ContainsKey(spec.Id) || detected.Contains(spec.Id) || spec.VendorFamily is null)
                    continue;
                if (_detectLocalCli(spec.VendorFamily))
                    detected.Add(spec.Id);
            }
        }

        if (detected.Count > 0 && _enablement.EnsureEnabled(detected))
            saved = new Dictionary<string, bool>(_enablement.ReadAll(ct), StringComparer.Ordinal);

        // The same one on/off decision the settings view uses.
        return ProviderCatalog.Known
            .Where(spec => ProviderEnablementStore.IsEnabled(saved, spec.Id, detected.Contains(spec.Id)))
            .ToArray();
    }

    private (IReadOnlyList<UpdateItem> Tools, bool AdaptersNewer, bool Repair) SelectToolsToUpdate(
        IReadOnlyList<UpdateItem> localItems,
        IReadOnlyList<ProviderSpec> enabledSpecs)
    {
        var enabledAdapters = enabledSpecs
            .Select(spec => spec.AdapterPackage)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var enabledFamilies = enabledSpecs
            .Select(spec => spec.VendorFamily)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var agentsById = _launcher.Agents
            .GroupBy(agent => agent.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var adaptersNewer = localItems.Any(item => item.Kind == "adapter" && item.State == UpdateItem.Newer);
        var selected = new List<UpdateItem>();
        var hasAdapterItem = false;

        foreach (var item in localItems)
        {
            if (item.Kind == "adapter" && item.State == UpdateItem.Behind && enabledAdapters.Contains(item.Name))
            {
                selected.Add(item);
                hasAdapterItem = true;
            }
            else if (item.Kind == "cli" && item.State == UpdateItem.Behind && item.Wanted is not null && enabledFamilies.Contains(item.Name))
            {
                selected.Add(item);
            }
        }

        // Repair when an enabled provider is missing from agents.json or its rooted adapter entrypoint is missing on disk.
        var repair = false;
        if (!hasAdapterItem && !adaptersNewer)
        {
            foreach (var spec in enabledSpecs)
            {
                if (spec.AdapterPackage is null) continue;
                var missingDescriptor = !agentsById.TryGetValue(spec.Id, out var agent);
                var missingEntryFile = agent is not null
                    && agent.Args.Count > 0
                    && Path.IsPathFullyQualified(agent.Args[0])
                    && !File.Exists(agent.Args[0]);
                if (missingDescriptor || missingEntryFile)
                {
                    selected.Add(new UpdateItem("adapter", spec.AdapterPackage, null, null, UpdateItem.Behind));
                    repair = true;
                    break;
                }
            }
        }

        return (selected, adaptersNewer, repair);
    }

    private static IReadOnlyList<UpdateItem> SelectBehindClis(
        IReadOnlyList<UpdateItem> localItems,
        IReadOnlyList<ProviderSpec> enabledSpecs)
    {
        var enabledFamilies = enabledSpecs
            .Select(spec => spec.VendorFamily)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return localItems
            .Where(item => item.Kind == "cli" && item.State == UpdateItem.Behind && item.Wanted is not null && enabledFamilies.Contains(item.Name))
            .ToArray();
    }

    private void TriggerLocalProbes(IReadOnlyList<ProviderSpec> enabledSpecs, CancellationToken ct)
    {
        if (!_probeAfterCheck || ct.IsCancellationRequested)
            return;
        var enabledIds = enabledSpecs.Select(spec => spec.Id).ToHashSet(StringComparer.Ordinal);
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var agent in _launcher.Agents)
        {
            if (!enabledIds.Contains(agent.Id) || agent.Invalid() is not null)
                continue;
            string account;
            try { account = AccountContext.Of(agent, Environment.GetEnvironmentVariable); }
            catch (CallFailed) { continue; }
            var entityId = $"{agent.Id}/{account}";
            ((IEngineRuntime)_engine).Write(scope =>
            {
                var expectedVersion = scope.QueryFirst(
                    "SELECT version FROM engine_provider WHERE id = $id;",
                    row => row.Long(0),
                    new SqlArg("$id", entityId));
                var context = new CommandContext(
                    Guid.NewGuid(),
                    _engine.Machine,
                    new EngineActor(ActorKind.System, "settings.reconcile"),
                    entityId,
                    expectedVersion,
                    0,
                    nowMs);
                return _engine.Commands.Apply(scope, context, new CheckProvider());
            });
        }
    }

    private void PublishWorking(bool working) =>
        _topics.Publish(Topic, CurrentKey, JsonSerializer.Serialize(new ReconcileWorking(working), SettingsReconcileJson.Default.ReconcileWorking));

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            running = _inFlight;
        }
        await _stop.CancelAsync();
        if (running is not null)
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReconcileArgs(string? Machine = null);

internal sealed record ReconcileResult(bool Started);

internal sealed record ReconcileWorking(bool Working);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReconcileArgs))]
[JsonSerializable(typeof(ReconcileResult))]
[JsonSerializable(typeof(ReconcileWorking))]
internal sealed partial class SettingsReconcileJson : JsonSerializerContext;
