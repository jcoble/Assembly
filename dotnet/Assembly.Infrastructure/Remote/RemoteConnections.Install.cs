using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteConnections
{
    internal SavedMachine? SavedProfile(string name, CancellationToken ct) => _store.Get(name, ct);
}
