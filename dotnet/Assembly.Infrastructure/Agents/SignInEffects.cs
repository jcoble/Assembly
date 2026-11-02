using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Agents;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Owns only live sign-in work; the Engine rows and original begin claim remain authoritative.</summary>
public sealed class SignInEffects : IEngineEffects, IAsyncDisposable
{
    private readonly IAppDatabase _db;
    private readonly SignInRunner _runner;
    private readonly IAgentLauncher _launcher;
    private readonly IProviderAdapters _adapters;
    private readonly ChallengeBoard _board;
    private readonly EngineRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly EngineAuthSink _sink;
    private readonly object _gate = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private Task? _dispose;
    private bool _closed;

    public SignInEffects(IAppDatabase db, SignInRunner runner, IAgentLauncher launcher,
        IProviderAdapters adapters, ChallengeBoard board, EngineRuntime runtime, TimeProvider time)
    {
        _db = db; _runner = runner; _launcher = launcher; _adapters = adapters;
        _board = board; _runtime = runtime; _time = time; _sink = new(runtime, time);
        _board.Changed += ChallengeChanged;
    }

    public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (effect.Kind is not ("begin_sign_in" or "cancel_sign_in")) return Failure("effect_not_available", EffectDisposition.Unavailable);
        var row = Read(effect, cancellationToken);
        if (row is null) return Failure("stale_attempt");
        if (effect.Kind == "cancel_sign_in") return await CancelAsync(row);
        if (!Active(row)) return Failure("interrupted");
        Attempt attempt;
        lock (_gate)
        {
            if (_closed) return Failure("interrupted");
            if (_attempts.TryGetValue(row.Operation, out var existing)) attempt = existing;
            else
            {
                // Recheck after admission and before registration: an already committed cancel must win.
                if (!Active(Read(effect, cancellationToken))) return Failure("interrupted");
                var capture = ConfigTargetIdentity.Capture(_launcher, row.Provider);
                if (capture.Descriptor.Invalid() is not null || AccountContext.Of(capture.Descriptor,
                    key => capture.Environment.GetValueOrDefault(key)) != row.Account) return Failure("invalid_descriptor");
                var environment = capture.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, ConfigTargetIdentity.EnvironmentComparer);
                var descriptor = capture.Descriptor with { Env = environment };
                attempt = new(row, descriptor, environment, capture.TargetId, _runtime.Stopping);
                _attempts.Add(row.Operation, attempt);
                attempt.Supervisor = SuperviseAsync(attempt);
            }
        }
        if (attempt.Row.Binding != row.Binding) return Failure("stale_attempt");
        if (Interlocked.Exchange(ref attempt.Starting, 1) != 0)
            return await attempt.Registered.Task is not null ? Confirm(row.Operation, "started") : Failure("interrupted");
        try
        {
            attempt.Capture = await _adapters.CaptureAsync(row.Provider, attempt.ConfigurationTarget, attempt.Stop.Token);
            if (!Current(attempt)) return Failure("interrupted");
            ConfigTargetIdentity.Validate(_launcher, attempt.Descriptor, attempt.Environment);
            var handle = await _runner.StartAsync(attempt.Descriptor, row.Method, row.Operation, attempt.Stop.Token);
            lock (_gate) attempt.Handle = handle; // The lazy Completion is read only by the already-owned supervisor.
            attempt.Registered.TrySetResult(handle);
            if (!Current(attempt)) await EndAsync(attempt);
            return Confirm(row.Operation, "started");
        }
        catch (OperationCanceledException) when (attempt.Stop.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return Failure("interrupted");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Progress(attempt, "failed", "adapter_error");
            return Failure("adapter_error");
        }
        finally { attempt.Registered.TrySetResult(null); }
    }

    public async Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = Read(effect, cancellationToken);
        if (row is null) return Failure("stale_attempt");
        if (effect.Kind == "cancel_sign_in") return await CancelAsync(row);
        if (effect.Kind != "begin_sign_in") return Failure("effect_not_available", EffectDisposition.Unavailable);
        Attempt? attempt;
        lock (_gate) _attempts.TryGetValue(row.Operation, out attempt);
        if (attempt is not null) await EndAsync(attempt);
        // A recovered claim cannot adopt an older runner's success or start another login.
        return Failure("interrupted");
    }

    private async Task<EffectReceipt> CancelAsync(OperationRow row)
    {
        if (row.State is not ("cancelled" or "expired" or "failed")) return Failure("stale_attempt");
        Attempt? attempt;
        lock (_gate) _attempts.TryGetValue(row.Operation, out attempt);
        if (attempt is not null)
        {
            if (attempt.Row.Target != row.Target) return Failure("stale_attempt");
            await EndAsync(attempt);
        }
        return Confirm(row.Operation, "stopped");
    }

    private async Task SuperviseAsync(Attempt attempt)
    {
        try
        {
            var handle = await attempt.Registered.Task;
            if (handle is null || !Current(attempt)) return;
            ConfigTargetIdentity.Validate(_launcher, attempt.Descriptor, attempt.Environment);
            var end = await handle.Completion;
            await attempt.Progress;
            if (!Current(attempt)) return;
            if (end == SignInEnd.SignedIn)
            {
                if (!Progress(attempt, "finishing", null) || !Current(attempt)) return;
                ConfigTargetIdentity.Validate(_launcher, attempt.Descriptor, attempt.Environment);
                if (attempt.Capture is { } capture) await _adapters.RetireAsync(capture, attempt.Stop.Token);
                if (Current(attempt)) Progress(attempt, "signed_in", null);
            }
            else Progress(attempt, "failed", end switch
            {
                SignInEnd.Refused => "unsupported",
                SignInEnd.AdapterCrashed => "adapter_crashed",
                SignInEnd.LinkLost => "adapter_error",
                SignInEnd.Cancelled => "interrupted",
                _ => "adapter_error"
            });
        }
        catch (OperationCanceledException) when (attempt.Stop.IsCancellationRequested || _runtime.Stopping.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Progress(attempt, "failed", "adapter_error"); }
        finally { await CleanupAsync(attempt); }
    }

    private void ChallengeChanged(string operation)
    {
        if (_board.Current(operation) is null) return; // ACK, cancellation and cleanup are not a new waiting generation.
        lock (_gate)
        {
            if (_closed || !_attempts.TryGetValue(operation, out var attempt) || attempt.Ending) return;
            attempt.Progress = WaitingAsync(attempt, attempt.Progress);
        }
    }

    private async Task WaitingAsync(Attempt attempt, Task previous)
    {
        await Task.Yield(); // Do not call the Engine writer from inside a runner/board callback's gate.
        await previous;
        Progress(attempt, "waiting_for_person", null);
    }

    private bool Progress(Attempt attempt, string state, string? reason)
    {
        try
        {
            return Current(attempt) && _sink.Progress(attempt.Row.Binding, state, reason).Applied;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private bool Current(Attempt attempt)
    {
        lock (_gate)
            if (_closed || attempt.Ending || !_attempts.TryGetValue(attempt.Row.Operation, out var current) || current != attempt) return false;
        return Active(Read(attempt.Row.Begin, attempt.Stop.Token));
    }

    private async Task EndAsync(Attempt attempt)
    {
        await CleanupAsync(attempt);
        await attempt.Supervisor;
    }

    private Task CleanupAsync(Attempt attempt)
    {
        lock (_gate)
        {
            if (attempt.Cleanup is not null) return attempt.Cleanup;
            attempt.Ending = true;
            return attempt.Cleanup = CleanAsync(attempt);
        }
    }

    private async Task CleanAsync(Attempt attempt)
    {
        await Task.Yield(); // Publish the idempotent cleanup task before invoking cancellation callbacks.
        try
        {
            await attempt.Stop.CancelAsync();
            var handle = await attempt.Registered.Task;
            if (handle is not null) await handle.DisposeAsync();
            _board.Clear(attempt.Row.Operation);
            await attempt.Progress;
        }
        finally
        {
            attempt.Stop.Dispose();
            lock (_gate)
                if (_attempts.GetValueOrDefault(attempt.Row.Operation) == attempt) _attempts.Remove(attempt.Row.Operation);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
            _board.Changed -= ChallengeChanged;
            return new(_dispose ??= DisposeCoreAsync(_attempts.Values.ToArray()));
        }
    }

    private async Task DisposeCoreAsync(Attempt[] attempts)
    {
        await Task.Yield();
        await Task.WhenAll(attempts.Select(EndAsync));
    }

    private OperationRow? Read(Effect effect, CancellationToken ct)
    {
        if (effect.SessionId is not null || effect.RunId is not null || effect.Attempt <= 0 || effect.Kind is not ("begin_sign_in" or "cancel_sign_in")) return null;
        try
        {
            var operation = effect.Kind == "begin_sign_in"
                ? effect.Payload.Deserialize(EngineJson.Default.BeginSignInEffect)?.OperationId
                : effect.Payload.Deserialize(EngineJson.Default.CancelSignInEffect)?.OperationId;
            if (string.IsNullOrWhiteSpace(operation) || effect.Id != effect.Kind + "/" + operation) return null;
            using var snapshot = _db.OpenSnapshot(ct);
            var row = snapshot.QueryFirst("""
                SELECT o.version,o.machine,o.provider,o.account_context,o.method_id,o.state,o.deadline_ms,
                       p.machine,p.provider,p.account_context,e.machine,e.kind,e.session_id,e.run_id,
                       e.payload_json,e.attempt,e.state,e.order_key
                FROM engine_sign_in o JOIN engine_provider p ON p.id=o.provider||'/'||o.account_context
                JOIN engine_outbox e ON e.id=$effect WHERE o.id=$operation
                """, r => new
            {
                Version = r.Long(0),
                Machine = r.Text(1),
                Provider = r.Text(2),
                Account = r.Text(3),
                Method = r.IsNull(4) ? null : r.Text(4),
                State = r.Text(5),
                Deadline = r.Long(6),
                ProviderMachine = r.Text(7),
                ProviderKey = r.Text(8),
                ProviderAccount = r.Text(9),
                EffectMachine = r.Text(10),
                Kind = r.Text(11),
                Sessionless = r.IsNull(12) && r.IsNull(13),
                Payload = r.Text(14),
                Attempt = r.Long(15),
                EffectState = r.Text(16),
                Order = r.Text(17)
            }, new SqlArg("$effect", effect.Id), new("$operation", operation));
            if (row is null || row.Machine != _runtime.Machine || row.ProviderMachine != row.Machine || row.EffectMachine != row.Machine
                || row.ProviderKey != row.Provider || row.ProviderAccount != row.Account || row.Kind != effect.Kind || !row.Sessionless
                || row.Attempt != effect.Attempt || row.EffectState is not ("inflight" or "done")
                || row.Order != "provider/" + row.Provider + "/" + row.Account || effect.OrderKey != row.Order) return null;
            using var document = JsonDocument.Parse(row.Payload);
            var payload = document.RootElement;
            if (!JsonElement.DeepEquals(payload, effect.Payload)) return null;
            if (effect.Kind == "begin_sign_in" && payload.Deserialize(EngineJson.Default.BeginSignInEffect) != new BeginSignInEffect(operation, row.Provider, row.Method)) return null;
            return new(operation, row.Provider, row.Account, row.Method, row.State, row.Version, row.Deadline,
                new(operation, row.Provider + "/" + row.Account, "begin_sign_in/" + operation, effect.Attempt), effect);
        }
        catch (JsonException) { return null; }
    }

    private bool Active(OperationRow? row) => row is not null && row.State is "starting" or "waiting_for_person" or "finishing"
        && row.Deadline > _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static EffectReceipt Confirm(string operation, string state) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new SignInEffectData(operation, state), EngineJson.Default.SignInEffectData), null);
    private static EffectReceipt Failure(string reason, EffectDisposition disposition = EffectDisposition.Failed) =>
        new(disposition, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), reason);

    private sealed record OperationRow(string Operation, string Provider, string Account, string? Method, string State,
        long Version, long Deadline, SignInAttemptBinding Binding, Effect Begin)
    {
        public string Target => Provider + "/" + Account;
    }

    private sealed class Attempt(OperationRow row, AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
        string configurationTarget, CancellationToken runtimeStop)
    {
        public OperationRow Row { get; } = row;
        public AgentDescriptor Descriptor { get; } = descriptor;
        public IReadOnlyDictionary<string, string> Environment { get; } = environment;
        public string ConfigurationTarget { get; } = configurationTarget;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(runtimeStop);
        public TaskCompletionSource<ISignInSession?> Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISignInSession? Handle { get; set; }
        public ProviderAdapterCapture? Capture { get; set; }
        public Task Supervisor { get; set; } = Task.CompletedTask;
        public Task Progress { get; set; } = Task.CompletedTask;
        public Task? Cleanup { get; set; }
        public bool Ending { get; set; }
        public int Starting;
    }
}
