using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;

namespace Assembly.Infrastructure.Engine.Auth;

/// <summary>One frozen effect owner map; send acknowledgments stay with the existing adapter projector.</summary>
public sealed class EffectRouter : IEngineEffects, IAsyncDisposable
{
    private readonly AdapterEffects _adapter;
    private readonly Dictionary<string, IEngineEffects> _handlers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _sealed;
    private Task? _dispose;
    private static readonly EffectReceipt NoHandler = new(EffectDisposition.Unavailable,
        JsonSerializer.SerializeToElement((string?)null, global::Assembly.Engine.EngineJson.Default.String), "no_handler");

    public EffectRouter(AdapterEffects adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        foreach (var kind in new[] { "start", "stop", "send", "answer", "handoff" }) _handlers.Add(kind, adapter);
    }

    public EffectRouter Add(string kind, IEngineEffects handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_sealed || _dispose is not null) throw new InvalidOperationException("Effect owners are frozen.");
            if (kind is not ("probe_provider" or "begin_sign_in" or "cancel_sign_in" or "workflow_setup" or "workflow_workspace")) throw new ArgumentException("Unsupported effect registration.", nameof(kind));
            if (!_handlers.TryAdd(kind, handler)) throw new InvalidOperationException("Effect already has an owner.");
        }
        return this;
    }

    internal void Seal() { lock (_gate) { if (_dispose is not null) throw new ObjectDisposedException(nameof(EffectRouter)); _sealed = true; } }

    public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _handlers.TryGetValue(effect.Kind, out var owner) ? owner.ExecuteAsync(effect, cancellationToken) : Task.FromResult(NoHandler);
    }

    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _handlers.TryGetValue(effect.Kind, out var owner) ? owner.ReconcileAsync(effect, cancellationToken) : Task.FromResult(NoHandler);
    }

    internal MessageOutcome? ProjectCompletion(IWriteScope scope, CommandContext context, Effect effect,
        EffectReceipt receipt, MessageDelivery? delivery) => effect.Kind == "send"
        ? _adapter.ProjectCompletion(scope, context, effect, receipt, delivery ?? throw new CallFailed("delivery_ack_conflict")) : null;

    public ValueTask DisposeAsync() { lock (_gate) return new(_dispose ??= DisposeOwnersAsync()); }

    private async Task DisposeOwnersAsync()
    {
        List<Exception> errors = [];
        foreach (var owner in _handlers.Values.Distinct<IEngineEffects>(ReferenceEqualityComparer.Instance))
        {
            try
            {
                if (owner is IAsyncDisposable asyncOwner) await asyncOwner.DisposeAsync();
                else if (owner is IDisposable disposable) disposable.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
