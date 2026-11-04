using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteConnections
{
    internal SavedMachine? SavedProfile(string name, CancellationToken ct) => _store.Get(name, ct);

    internal async Task<RemoteInstallBinding> CaptureInstallOwnerAsync(string name, string engine, CancellationToken ct)
    {
        var snapshot = _store.CaptureProfile(name, ct) ?? throw InstallChanged();
        var owner = await CaptureProjectOwnerAsync(name, ct);
        if (owner.Engine != engine || owner.Profile != snapshot.Profile) throw InstallChanged();
        void Profile(CancellationToken token)
        {
            if (_store.CaptureProfile(name, token) != snapshot) throw InstallChanged();
        }
        void Current(CancellationToken token) { Profile(token); RequireProjectOwner(owner, token); }
        void Admission(IWriteScope scope)
        {
            RequireProjectOwner(scope, owner);
            if (scope.QueryFirst("SELECT profile_generation FROM saved_machines WHERE name=$name", row => row.Text(0), new SqlArg("$name", name)) != snapshot.Generation)
                throw InstallChanged();
        }
        Current(ct);
        return new(snapshot.Profile, engine, Current, Profile, Admission);
    }

    private static CallFailed InstallChanged() => new("The selected remote owner changed.", "target_changed");
}
