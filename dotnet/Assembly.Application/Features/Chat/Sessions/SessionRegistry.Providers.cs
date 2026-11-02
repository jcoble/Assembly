using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionRegistry : IProviderAdapters
{
    internal SharedAdapter Adapter(string agentId) =>
        _adapters.GetOrAdd(agentId, id => new SharedAdapter(_launcher, id, _time, _log));

    Task<ProviderProbeResult> IProviderAdapters.ProbeAsync(AgentDescriptor descriptor, string configurationTargetId,
        string probeFolder, CancellationToken ct) => Adapter(descriptor.Id).ProbeAsync(descriptor, configurationTargetId, probeFolder, ct);

    async Task<ProviderAdapterCapture?> IProviderAdapters.CaptureAsync(string provider, string configurationTargetId, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(provider, out var adapter)) return null;
        var running = await adapter.CaptureAsync(configurationTargetId, ct);
        return running is null ? null : new Capture(this, adapter, running);
    }

    async Task IProviderAdapters.RetireAsync(ProviderAdapterCapture capture, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (capture is not Capture owned || owned.Owner != this)
            throw new CallFailed("The selected adapter is no longer available.");
        await owned.Adapter.RetireAsync(owned.Running, null);
    }

    private sealed class Capture(SessionRegistry owner, SharedAdapter adapter, RunningAdapter running) : ProviderAdapterCapture
    {
        public SessionRegistry Owner { get; } = owner;
        public SharedAdapter Adapter { get; } = adapter;
        public RunningAdapter Running { get; } = running;
    }
}
