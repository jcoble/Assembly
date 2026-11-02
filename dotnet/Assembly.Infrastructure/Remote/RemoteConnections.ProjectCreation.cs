using System.Net.WebSockets;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteConnections
{
    internal sealed class ProjectOwner
    {
        internal SavedMachine Profile { get; }
        internal RemoteMachine Machine { get; }
        internal string Engine { get; }
        internal ClientWebSocket Socket { get; }
        internal RemoteMachine.OwnerConnectionBinding Binding { get; }
        internal ProjectOwner(SavedMachine profile, RemoteMachine machine, string engine, ClientWebSocket socket,
            RemoteMachine.OwnerConnectionBinding binding)
            => (Profile, Machine, Engine, Socket, Binding) = (profile, machine, engine, socket, binding);
    }

    internal async Task<ProjectOwner> CaptureProjectOwnerAsync(string name, CancellationToken ct)
    {
        var profile = _store.Get(name, ct) ?? throw Changed();
        if (!_active.TryGetValue(name, out var active) || active.Profile != profile || !ReferenceEquals(_machines.Named(name), active.Machine))
            throw Changed();
        var read = await active.Machine.CaptureOwnerReadAsync(null, ct);
        var binding = await active.Machine.CaptureOwnerBindingAsync(read.Identity, ct);
        var owner = new ProjectOwner(profile, active.Machine, read.Identity, read.Socket, binding);
        RequireProjectOwner(owner, ct);
        var current = await active.Machine.CaptureOwnerReadAsync(read.Identity, ct);
        if (!ReferenceEquals(current.Socket, read.Socket)) throw Changed();
        RequireProjectOwner(owner, ct);
        return owner;
    }

    internal void RequireProjectOwner(ProjectOwner owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireActive(owner);
        if (_store.Get(owner.Profile.Name, ct) != owner.Profile) throw Changed();
        RequireActive(owner);
    }

    // Only captured owner/profile facts are checked in the supplied DB writer; no network or filesystem I/O.
    internal void RequireProjectOwner(IWriteScope scope, ProjectOwner owner)
    {
        RequireActive(owner);
        var profile = scope.QueryFirst("SELECT name,ssh,port,token_file FROM saved_machines WHERE name=$name",
            row => new SavedMachine(row.Text(0), row.Text(1), (int)row.Long(2), row.Text(3)), new SqlArg("$name", owner.Profile.Name));
        if (profile != owner.Profile) throw Changed();
        RequireActive(owner);
    }
    private void RequireActive(ProjectOwner owner)
    {
        if (_disposed || owner.Binding.IsInvalidated || owner.Socket.State != WebSocketState.Open ||
            !_active.TryGetValue(owner.Profile.Name, out var active) || active.Profile != owner.Profile ||
            !ReferenceEquals(active.Machine, owner.Machine) || !ReferenceEquals(_machines.Named(owner.Profile.Name), owner.Machine))
            throw Changed();
    }
    private static CallFailed Changed() => new("The selected machine connection or profile changed. No project was added; inspect the folder before trying Add folder.", "project_owner_changed");
}
