using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Sessions;

/// <summary>
/// One captured owner for the existing rows protocol. A remote session's rows are read from its saved copy here, under
/// the copy guard, online or offline (TSK-1692); a call that acts on the remote captures its live owner instead.
/// </summary>
internal sealed class SessionRowSources(IAppDatabase db, SessionStore sessions, MachineSet machines,
    RemoteConnections? connections = null) : IRowSourceBindings
{
    public async Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source.Machine)) throw Changed();
        EngineGuard guard;
        var local = db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), ct);
        if (source.Machine == local)
        {
            if (source.Generation != 0 || source.RouteMachine is not null) throw Changed();
            guard = new(() =>
            {
                if (db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), default) != source.Machine)
                    throw Changed();
            }, null);
        }
        else
        {
            if (connections is null) throw Changed();
            var captured = await connections.CaptureSessionOpenSourceAsync(db, source, ct);
            guard = new(captured.EnsureCurrent, captured.Live);
        }
        try { ct.ThrowIfCancellationRequested(); guard.EnsureCurrent(); return guard; }
        catch { guard.Dispose(); throw; }
    }

    private sealed class EngineGuard : IExpectedSourceGuard
    {
        private readonly Action _check;
        private readonly EngineMirror.LiveMirrorBinding? _live;
        private readonly CancellationTokenSource? _changed;
        private readonly CancellationToken _token;
        private int _disposed;
        internal EngineGuard(Action check, EngineMirror.LiveMirrorBinding? live)
        {
            (_check, _live) = (check, live);
            _changed = live is null ? null : CancellationTokenSource.CreateLinkedTokenSource(live.Invalidated, live.OwnerBinding.Invalidated);
            _token = _changed?.Token ?? CancellationToken.None;
        }
        public CancellationToken Changed => _token;
        public void EnsureCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0 || _token.IsCancellationRequested || _live?.IsInvalidated == true) throw SessionRowSources.Changed();
            _check();
        }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _changed?.Dispose(); }
    }

    internal sealed record CommandBinding(RowSourceBinding Guard, SessionInfo Session, IMachine Machine,
        EngineMirror.LiveMirrorBinding? Live);

    internal async Task<CommandBinding> CaptureCommandAsync(ExpectedSource source, string session, CancellationToken ct)
    {
        var guard = await CaptureLiveAsync(source, session, ct);
        try
        {
            guard.EnsureDisplayCurrent();
            var info = sessions.Get(session) ?? throw Changed();
            return guard switch
            {
                LocalBinding local => new(guard, info, local.CapturedMachine, null),
                RemoteBinding remote => new(guard, info, remote.CapturedMachine, remote.Live),
                _ => throw Changed()
            };
        }
        catch { guard.Dispose(); throw; }
    }

    /// <summary>Rows reads: a local session's store, or a remote session's saved copy. Never the remote itself.</summary>
    public Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct) =>
        Task.FromResult<RowSourceBinding>(IsLocal(source, session, ct) ? CaptureLocal(source, session) : CaptureCopy(source, session));

    /// <summary>A call that acts on the session's machine: the local one, or the remote's live owner.</summary>
    public async Task<RowSourceBinding> CaptureLiveAsync(ExpectedSource source, string session, CancellationToken ct)
    {
        if (IsLocal(source, session, ct)) return CaptureLocal(source, session);
        if (connections is null) throw Changed();
        var captured = await connections.CaptureSessionOpenSourceAsync(db, source, ct);
        ct.ThrowIfCancellationRequested();
        return CapturedRemote(captured.Machine, captured.Live, captured.EnsureCurrent, scope => CheckDisplay(source, session, scope));
    }

    private bool IsLocal(ExpectedSource source, string session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source.Machine) || string.IsNullOrWhiteSpace(session)) throw Changed();
        return source.Machine == db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), ct);
    }

    private LocalBinding CaptureLocal(ExpectedSource source, string session)
    {
        if (source.Generation != 0 || source.RouteMachine is not null) throw Changed();
        var info = sessions.Get(session) ?? throw new CallFailed("Unknown session.", "unknown_session");
        var machine = machines.Named(null) ?? throw Changed();
        void Check()
        {
            if (sessions.Get(session) is not { Machine: null } ||
                db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), default) != source.Machine ||
                db.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", row => row.Text(0),
                    default, new SqlArg("$id", session)) is { } owner && owner != source.Machine)
                throw Changed();
        }
        Check();
        return new LocalBinding(machine, info, Check, scope => CheckDisplay(source, session, scope));
    }

    // A saved copy needs no connection: only that it is still this source's (CopyGuard).
    private CopyBinding CaptureCopy(ExpectedSource source, string session)
    {
        void Check(IReadScope? scope)
        {
            using var snapshot = scope is null ? db.OpenSnapshot(default) : null;
            CopyGuard.Check(scope ?? snapshot!, sessions, source, session);
        }
        Check(null);
        var info = sessions.Get(session) ?? throw Changed();
        return new CopyBinding(machines.Named(source.RouteMachine), info, Check);
    }

    // The live display identity a remote session's command certifies: the mirror it was opened from, still live.
    private void CheckDisplay(ExpectedSource source, string session, IReadScope? scope)
    {
        using var snapshot = scope is null ? db.OpenSnapshot(default) : null;
        var read = scope ?? snapshot!;
        if (source.RouteMachine is null
            ? source.Generation != 0 || read.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != source.Machine
            : source.Generation <= 0 || read.QueryFirst("""
                SELECT COUNT(*) FROM engine_mirror WHERE machine=$machine AND name=$name
                    AND generation=$generation AND state='live'
                """, row => row.Long(0), new SqlArg("$machine", source.Machine), new("$name", source.RouteMachine),
                new("$generation", source.Generation)) != 1)
            throw Changed();
        var info = sessions.Get(read, session);
        if (info is null || info.SessionId != session || info.Machine != source.RouteMachine ||
            read.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", row => row.Text(0),
                new SqlArg("$id", session)) is { } owner && owner != source.Machine)
            throw Changed();
    }

    /// <summary>The live owner of a call that acts on the remote: it changes with the mirror or its connection.</summary>
    internal static RowSourceBinding CapturedRemote(RemoteMachine machine, EngineMirror.LiveMirrorBinding live, Action ensureCurrent,
        Action<IReadScope?>? certifyDisplay = null) => new RemoteBinding(machine, live, ensureCurrent, certifyDisplay);

    private sealed class LocalBinding(IMachine machine, SessionInfo session, Action check, Action<IReadScope?> certifyDisplay) : RowSourceBinding
    {
        internal IMachine CapturedMachine => machine;
        public override CancellationToken Changed => CancellationToken.None;
        public override void EnsureCurrent() => check();
        public override void EnsureDisplayCurrent(IReadScope? scope = null) { EnsureCurrent(); certifyDisplay(scope); }
        public override void Watch() { EnsureCurrent(); machine.Watch(session); }
        public override string? ActiveRun { get { EnsureCurrent(); return machine.ActiveRun(session); } }
        public override void Dispose() { }
    }

    // A remote session's saved copy. Its machine, when configured here, follows it while it's open; offline it doesn't.
    private sealed class CopyBinding(IMachine? machine, SessionInfo session, Action<IReadScope?> check) : RowSourceBinding
    {
        public override CancellationToken Changed => CancellationToken.None;
        public override void EnsureCurrent() => check(null);
        public override void EnsureDisplayCurrent(IReadScope? scope = null) => check(scope);
        public override void Watch() { EnsureCurrent(); machine?.Watch(session); }
        public override string? ActiveRun { get { EnsureCurrent(); return machine?.ActiveRun(session); } }
        public override void Dispose() { }
    }

    private sealed class RemoteBinding : RowSourceBinding
    {
        internal RemoteMachine CapturedMachine => _machine;
        internal EngineMirror.LiveMirrorBinding Live => _live;
        private readonly RemoteMachine _machine;
        private readonly EngineMirror.LiveMirrorBinding _live;
        private readonly Action _check;
        private readonly Action<IReadScope?>? _certifyDisplay;
        private readonly CancellationTokenSource _changed;
        private readonly CancellationToken _token;
        private int _disposed;
        public RemoteBinding(RemoteMachine machine, EngineMirror.LiveMirrorBinding live, Action check, Action<IReadScope?>? certifyDisplay)
        {
            (_machine, _live, _check) = (machine, live, check);
            _certifyDisplay = certifyDisplay;
            _changed = CancellationTokenSource.CreateLinkedTokenSource(live.Invalidated, live.OwnerBinding.Invalidated);
            _token = _changed.Token;
        }
        public override CancellationToken Changed => _token;
        public override void EnsureCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0 || _live.IsInvalidated || _token.IsCancellationRequested) throw SessionRowSources.Changed();
            _check();
        }
        public override void EnsureDisplayCurrent(IReadScope? scope = null)
        {
            EnsureCurrent();
            if (_certifyDisplay is null) throw SessionRowSources.Changed();
            _certifyDisplay(scope);
        }
        // The saved copy's binding follows the session; a live command guard has nothing to follow.
        public override void Watch() => EnsureCurrent();
        public override string? ActiveRun => null;
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _changed.Dispose();
        }
    }

    private static CallFailed Changed() => new("The selected row source changed.", "source_changed");
}
