using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task<OwnerConnectionBinding> CaptureTerminalOwnerAsync(string? expectedEngine, CancellationToken ct)
    {
        var read = await CaptureOwnerReadAsync(null, ct);
        if (expectedEngine is not null && expectedEngine != read.Identity)
            throw new CallFailed("The terminal's original owning Engine changed.", "source_changed");
        return await CaptureOwnerBindingAsync(read.Identity, ct);
    }
}
