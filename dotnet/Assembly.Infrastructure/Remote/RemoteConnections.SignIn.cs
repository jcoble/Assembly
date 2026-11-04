using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteConnections
{
    internal async Task<(string Ssh, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent)> CaptureSignInCallbackAsync(
        IAppDatabase db, string engine, RemoteMachine expectedRemote, RemoteMachine.OwnerConnectionBinding expectedOwner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedOwner.IsInvalidated || expectedOwner.Machine != engine || !ReferenceEquals(ByEngineMachine(db, engine), expectedRemote))
            throw SignInSourceChanged();
        var owner = await CaptureProjectOwnerAsync(expectedRemote.Name!, ct);
        if (owner.Engine != engine || !ReferenceEquals(owner.Machine, expectedRemote) || !ReferenceEquals(owner.Binding, expectedOwner) ||
            !_active.TryGetValue(owner.Profile.Name, out var active)) throw SignInSourceChanged();
        var live = await active.Mirror.CaptureLiveBindingAsync(ct);
        if (live is null || live.Machine != engine || !ReferenceEquals(live.OwnerBinding, expectedOwner)) throw SignInSourceChanged();
        void EnsureCurrent()
        {
            try
            {
                if (live.IsInvalidated) throw SignInSourceChanged();
                RequireProjectOwner(owner, CancellationToken.None);
                if (!_active.TryGetValue(owner.Profile.Name, out var current) || !ReferenceEquals(current.Mirror, active.Mirror))
                    throw SignInSourceChanged();
            }
            catch (CallFailed) { throw SignInSourceChanged(); }
        }
        EnsureCurrent();
        return (owner.Profile.Ssh, live, EnsureCurrent);
    }

    private static CallFailed SignInSourceChanged() => new("The sign-in machine connection changed.", "source_changed");
}
