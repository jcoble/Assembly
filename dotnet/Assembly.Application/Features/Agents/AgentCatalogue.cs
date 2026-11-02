using Assembly.Application.Common.Agents;

namespace Assembly.Application.Features.Agents;

/// <summary>Owns the bounded work deadline; the adapter port owns and awaits its probe cleanup.</summary>
public sealed class AgentCatalogue(IProviderAdapters adapters, string probeFolder, TimeProvider time)
{
    public Task<ProviderFacts> CheckAsync(AgentDescriptor agent, CancellationToken ct) =>
        CheckCoreAsync(agent, null, ct);

    public Task<ProviderFacts> CheckAsync(AgentDescriptor agent,
        IReadOnlyDictionary<string, string> capturedEnvironment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capturedEnvironment);
        return CheckCoreAsync(agent, capturedEnvironment, ct);
    }

    private async Task<ProviderFacts> CheckCoreAsync(AgentDescriptor agent,
        IReadOnlyDictionary<string, string>? capturedEnvironment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (agent.Invalid() is not null)
            return Facts(new(null, null, null, false, "invalid_descriptor"));

        // Freeze both descriptor collections and the full effective environment before deriving identity.
        // The port launches this same snapshot, rather than reading the ambient environment again.
        var environment = (capturedEnvironment ?? ConfigTargetIdentity.EnvironmentOf(agent))
            .ToDictionary(pair => pair.Key, pair => pair.Value, ConfigTargetIdentity.EnvironmentComparer);
        var captured = agent with
        {
            Args = agent.Args.ToArray(),
            Air = agent.Air?.ToArray(),
            Env = environment
        };
        var target = ConfigTargetIdentity.Of(captured, environment);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var result = await adapters.ProbeAsync(captured, target, probeFolder, work.Token);
            ct.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            return Facts(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Facts(new(null, null, null, false, "timed_out"));
        }

        ProviderFacts Facts(ProviderProbeResult result) =>
            ProviderFactsClassifier.Classify(agent, result, time.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
