using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    internal async Task<RemoteCliLocator> ReadCliLocatorAsync(OwnerConnectionBinding binding, CancellationToken ct)
    {
        var current = await CaptureOwnerBindingAsync(binding.Machine, ct);
        if (!ReferenceEquals(current, binding)) throw new CallFailed("CLI configuration unavailable.", "selection_changed");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Invalidated);
        var result = await CallAsync("engine", "cli_locators", JsonSerializer.Deserialize("{}", EngineJson.Default.JsonElement),
            false, stop.Token, expectedEngineId: binding.Machine, expectedOwner: binding);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 16 * 1024)
            throw new CallFailed("CLI configuration unavailable.", "configuration_unavailable");
        RemoteCliLocator locator;
        try { locator = result.Deserialize(RemoteCliLocatorJson.Default.RemoteCliLocator) ?? throw new JsonException(); }
        catch (JsonException) { throw new CallFailed("CLI configuration unavailable.", "configuration_unavailable"); }
        locator.Validate();
        if (locator.MachineId != binding.Machine || !await IsCurrentOwnerBindingAsync(binding, stop.Token))
            throw new CallFailed("CLI configuration unavailable.", "selection_changed");
        return locator;
    }
}
