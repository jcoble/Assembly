using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Engine;
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
    internal async Task<Func<IWriteScope, SessionsFeature.Created>> CreateSessionAsync(IAppDatabase db,
        ProjectStore projects, string fromMachine, SessionsFeature.CreateArgs args, CallContext call)
    {
        if (args.Machine is not { Length: > 0 } route) throw Changed();
        using var stop = call.SourceGuard is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.SourceGuard.Changed);
        var ct = stop?.Token ?? call.Cancellation;
        var expected = args.ExpectedSource ?? call.ExpectedSource;
        var captured = await CaptureCreationOwner(db, route, expected, call, ct);
        ProjectRecord? project = null;
        if (args.ProjectId is not null)
        {
            using var snapshot = db.OpenSnapshot(ct);
            project = projects.Get(snapshot, args.ProjectId);
            if (project is null || project.RemovedAt is not null || project.Machine != route)
                throw new CallFailed("The selected project is unavailable on this owner.", "wrong_machine");
        }
        captured.Ensure();
        var request = args with { Machine = null, ExpectedSource = expected };
        var descriptor = project is null ? null : new SessionsFeature.CreateAtRootArgs(captured.Owner.Engine,
            fromMachine, request, project.Id, project.RootPath);
        SessionsFeature.Created accepted;
        try
        {
            accepted = await captured.Owner.Machine.CreateBoundSessionAsync(request, descriptor,
                captured.Owner.Binding, captured.Owner.Socket, captured.Live, ct,
                (scope, result) => Bookkeep(scope, result.ProjectId, result.AcceptedRoot));
        }
        catch (Exception error) when (error is MachineUnreachable or IOException or TimeoutException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Reconcile only on this exact owner; never resend create or reacquire a route after an unknown result.
            captured.Ensure();
            var known = await captured.Owner.Machine.ReadBoundCreatedSessionAsync(args.ClientId, expected is not null,
                captured.Owner.Binding, captured.Owner.Socket, captured.Live, ct,
                (scope, result) => Bookkeep(scope, result.ProjectId, result.AcceptedRoot));
            if (known.SessionId is null) throw;
            accepted = new(known.SessionId, known.CheckoutOperationId, known.ProjectId, known.AcceptedRoot, known.Facts);
        }
        captured.Ensure();
        return scope =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            captured.EnsureCurrent();
            RequireProjectOwner(scope, captured.Owner);
            return accepted;
        };
        void Bookkeep(IWriteScope scope, string? projectId, string? root)
        {
            captured.EnsureCurrent();
            RequireProjectOwner(scope, captured.Owner);
            TouchAcceptedProject(scope, projects, captured.Owner, projectId, root);
        }
    }

    internal async Task<SessionsFeature.ByClientReply> ReadCreatedSessionAsync(IAppDatabase db, ProjectStore projects,
        SessionsFeature.ByClientArgs args, CallContext call)
    {
        if (args.Machine is not { Length: > 0 } route) throw Changed();
        using var stop = call.SourceGuard is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.SourceGuard.Changed);
        var ct = stop?.Token ?? call.Cancellation;
        var expected = args.ExpectedSource ?? call.ExpectedSource;
        var captured = await CaptureCreationOwner(db, route, expected, call, ct);
        var accepted = await captured.Owner.Machine.ReadBoundCreatedSessionAsync(args.ClientId, expected is not null,
            captured.Owner.Binding, captured.Owner.Socket, captured.Live, ct, (scope, result) =>
            {
                captured.EnsureCurrent();
                RequireProjectOwner(scope, captured.Owner);
                // One device-local bookkeeping transaction with facade seeding, before title I/O.
                TouchAcceptedProject(scope, projects, captured.Owner, result.ProjectId, result.AcceptedRoot);
            });
        captured.Ensure();
        return accepted;
    }

    private static void TouchAcceptedProject(IWriteScope scope, ProjectStore projects, ProjectOwner owner,
        string? projectId, string? acceptedRoot)
    {
        // Missing/pruned or child-only root evidence certifies no desktop registry association.
        if (projectId is null || acceptedRoot is null) return;
        var project = projects.Get(scope, projectId);
        if (project is null || project.RemovedAt is not null || project.Machine != owner.Profile.Name || project.RootPath != acceptedRoot)
            throw new CallFailed("The accepted project association changed; the owner session still exists.", "source_changed");
        projects.Touch(scope, project.Id);
    }

    private async Task<(ProjectOwner Owner, EngineMirror.LiveMirrorBinding? Live, Action Ensure, Action EnsureCurrent)> CaptureCreationOwner(
        IAppDatabase db, string route, ExpectedSource? expected, CallContext call, CancellationToken ct)
    {
        call.SourceGuard?.EnsureCurrent();
        var owner = await CaptureProjectOwnerAsync(route, ct);
        EngineMirror.LiveMirrorBinding? live = null;
        Action? sourceCurrent = null;
        if (expected is not null)
        {
            if (expected.RouteMachine != route || expected.Machine != owner.Engine) throw Changed();
            var source = await CaptureSessionOpenSourceAsync(db, expected, ct);
            if (!ReferenceEquals(source.Machine, owner.Machine) || !ReferenceEquals(source.Live.OwnerBinding, owner.Binding) ||
                source.Live.Machine != expected.Machine || source.Live.Generation != expected.Generation) throw Changed();
            live = source.Live; sourceCurrent = source.EnsureCurrent;
        }
        void EnsureCurrent()
        {
            call.Cancellation.ThrowIfCancellationRequested();
            call.SourceGuard?.EnsureCurrent();
            sourceCurrent?.Invoke();
            RequireActive(owner);
        }
        void Ensure() { EnsureCurrent(); RequireProjectOwner(owner, call.Cancellation); }
        Ensure();
        return (owner, live, Ensure, EnsureCurrent);
    }

}
