using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Agents;

/// <summary>Private transport material for a bounded check, never a wire or durable catalogue payload.</summary>
public sealed record ProviderProbeResult(InitializeResponse? Initialize, NewSessionResponse? Session,
    AuthStatus? AuthStatus, bool AuthRequired, string? ProblemCode);

/// <summary>An exact adapter instance that only its owning registry can capture.</summary>
public abstract class ProviderAdapterCapture
{
    private protected ProviderAdapterCapture() { }
}

/// <summary>The existing shared adapter owner supplies bounded probes and guarded exact-instance retirement.</summary>
public interface IProviderAdapters
{
    Task<ProviderProbeResult> ProbeAsync(AgentDescriptor descriptor, string configurationTargetId,
        string probeFolder, CancellationToken ct);
    Task<ProviderAdapterCapture?> CaptureAsync(string provider, string configurationTargetId, CancellationToken ct);
    Task RetireAsync(ProviderAdapterCapture capture, CancellationToken ct);
}
