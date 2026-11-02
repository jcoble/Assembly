using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Agents;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Probes one exact configured target for its original persisted claim; the Engine owns facts and merging.</summary>
public sealed class ProviderEffects(IAppDatabase db, AgentCatalogue catalogue, IAgentLauncher launcher,
    EngineRuntime runtime, TimeProvider time) : IEngineEffects
{
    private readonly EngineAuthSink _sink = new(runtime, time);
    private readonly CancellationToken _stopping = runtime.Stopping;

    public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
        work.Token.ThrowIfCancellationRequested();
        var capture = Capture(effect, work.Token);
        if (capture is null) return Unknown();
        ProviderFacts? facts;
        try
        {
            facts = await catalogue.CheckAsync(capture.Descriptor, capture.Environment, work.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Only the fixed safe reason is retained; an unsuccessful transport produces no invented catalogue.
            facts = null;
        }
        work.Token.ThrowIfCancellationRequested();
        try
        {
            var current = ConfigTargetIdentity.RequireSameConfiguration(launcher, capture.Descriptor, capture.Environment);
            if (current.Name != capture.Descriptor.Name || current.Invalid() != capture.Descriptor.Invalid()) return Unknown();
        }
        catch (CallFailed) { return Unknown(); }
        work.Token.ThrowIfCancellationRequested();
        var safeFacts = facts is null ? JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String)
            : JsonSerializer.SerializeToElement(facts, ProvidersJson.Default.ProviderFacts);
        var recorded = await _sink.RecordCheckAsync(capture.Binding, safeFacts,
            facts is null ? "unknown" : InstallationOf(facts.Installation),
            facts is null ? "could_not_check" : AuthOf(facts.Auth),
            facts is null ? "adapter_error" : ReasonOf(facts.Problem), runtime.Stopping);
        return recorded.Applied
            ? new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
                new ProviderEffectData(capture.Binding.TargetId, capture.Binding.ProviderVersion), EngineJson.Default.ProviderEffectData), null)
            : Unknown();
    }

    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _stopping.ThrowIfCancellationRequested();
        // A newer provider row is not proof of this claim's callback. Reconciliation never starts another probe.
        return Task.FromResult(Unknown());
    }

    private CapturedProbe? Capture(Effect effect, CancellationToken ct)
    {
        if (effect.Kind != "probe_provider" || effect.SessionId is not null || effect.RunId is not null || effect.Attempt <= 0)
            return null;
        ProbeProviderEffect? intent;
        try { intent = effect.Payload.Deserialize(EngineJson.Default.ProbeProviderEffect); }
        catch (JsonException) { return null; }
        if (intent is null) return null;
        using var scope = db.OpenSnapshot(ct);
        var provider = ProviderState.Read(scope, intent.TargetId);
        if (provider is null || provider.Machine != runtime.Machine || provider.Target.Id != intent.TargetId)
            return null;
        var persisted = ProviderProbe.Read(scope, provider, effect.Id, effect.Attempt);
        if (persisted is null || persisted.Payload != intent || effect.OrderKey != "provider/" + provider.Id)
            return null;
        try
        {
            var captured = ConfigTargetIdentity.Capture(launcher, provider.Target.Provider);
            var account = AccountContext.Of(captured.Descriptor,
                name => captured.Environment.TryGetValue(name, out var value) ? value : null);
            if (account != provider.Target.AccountContext) return null;
            return new(new(provider.Id, effect.Id, intent.ProviderVersion, effect.Attempt), captured.Descriptor, captured.Environment);
        }
        catch (CallFailed) { return null; }
    }

    private static string InstallationOf(Installation installation) => installation switch
    {
        Installation.Installed => "installed",
        Installation.NotInstalled => "not_installed",
        _ => "unknown"
    };
    private static string AuthOf(AuthState auth) => auth switch
    {
        AuthState.SignedIn => "signed_in",
        AuthState.NeedsSignIn => "needs_sign_in",
        AuthState.Unverified => "unverified",
        AuthState.Unsupported => "unsupported",
        _ => "could_not_check"
    };
    private static string? ReasonOf(ProviderProblem? reason) => reason switch
    {
        null => null,
        ProviderProblem.not_installed => "not_installed",
        ProviderProblem.cannot_start => "cannot_start",
        ProviderProblem.invalid_descriptor => "invalid_descriptor",
        ProviderProblem.timed_out => "timed_out",
        ProviderProblem.adapter_crashed => "adapter_crashed",
        ProviderProblem.network => "network",
        ProviderProblem.version_unknown => "version_unknown",
        ProviderProblem.interrupted => "interrupted",
        _ => "adapter_error"
    };
    private static EffectReceipt Unknown() => new(EffectDisposition.Unknown,
        JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), "interrupted");
    private sealed record CapturedProbe(ProviderProbeBinding Binding, AgentDescriptor Descriptor,
        IReadOnlyDictionary<string, string> Environment);
}
