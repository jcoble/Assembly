using System.Collections.Concurrent;

namespace Assembly.Application.Features.Agents;

/// <summary>
/// The last check's scrubbed failure detail per provider target, held only in memory: raw output never
/// enters the engine's safe facts, so the settings view reads it here for this machine's providers.
/// A successful check clears the entry; a remote machine's detail never leaves that machine.
/// </summary>
public sealed class ProviderFailureDetails
{
    private readonly ConcurrentDictionary<string, string> _details = new(StringComparer.Ordinal);

    public void Set(string providerTargetId, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            _details.TryRemove(providerTargetId, out _);
        else
            _details[providerTargetId] = detail;
    }

    public string? Get(string providerTargetId) =>
        _details.TryGetValue(providerTargetId, out var detail) ? detail : null;
}
