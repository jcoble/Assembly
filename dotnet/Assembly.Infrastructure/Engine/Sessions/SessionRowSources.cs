using System.Runtime.CompilerServices;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Sessions;

/// <summary>One captured owner for the existing rows protocol; remote reads never use the display-copy cache.</summary>
internal sealed class SessionRowSources(IAppDatabase db, SessionStore sessions, MachineSet machines,
    RemoteConnections? connections = null) : IRowSourceBindings
{
    public async Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source.Machine) || string.IsNullOrWhiteSpace(session)) throw Changed();
        var local = db.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), ct);
        if (source.Machine == local)
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
            return new LocalBinding(machine, info, Check);
        }
        if (connections is null) throw Changed();
        var captured = await connections.CaptureSessionOpenSourceAsync(db, source, ct);
        ct.ThrowIfCancellationRequested();
        return CapturedRemote(session, captured.Machine, captured.Live, captured.EnsureCurrent);
    }

    internal static RowSourceBinding CapturedRemote(string session, RemoteMachine machine,
        EngineMirror.LiveMirrorBinding live, Action ensureCurrent) => new RemoteBinding(session, machine, live, ensureCurrent);

    private sealed class LocalBinding(IMachine machine, SessionInfo session, Action check) : RowSourceBinding
    {
        public override bool Remote => false;
        public override CancellationToken Changed => CancellationToken.None;
        public override void EnsureCurrent() => check();
        public override void Watch() { EnsureCurrent(); machine.Watch(session); }
        public override string? ActiveRun { get { EnsureCurrent(); return machine.ActiveRun(session); } }
        public override Task<JsonElement> PageAsync(long before, int limit, CancellationToken ct) => throw SessionRowSources.Changed();
        public override Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken ct) => throw SessionRowSources.Changed();
        public override IAsyncEnumerable<JsonElement> WatchAsync(long? after, CancellationToken ct) => throw SessionRowSources.Changed();
        public override void Dispose() { }
    }

    private sealed class RemoteBinding : RowSourceBinding
    {
        private readonly string _session;
        private readonly RemoteMachine _machine;
        private readonly EngineMirror.LiveMirrorBinding _live;
        private readonly Action _check;
        private readonly CancellationTokenSource _changed;
        private readonly CancellationToken _token;
        private int _disposed;
        public RemoteBinding(string session, RemoteMachine machine, EngineMirror.LiveMirrorBinding live, Action check)
        {
            (_session, _machine, _live, _check) = (session, machine, live, check);
            _changed = CancellationTokenSource.CreateLinkedTokenSource(live.Invalidated, live.OwnerBinding.Invalidated);
            _token = _changed.Token;
        }
        public override bool Remote => true;
        public override CancellationToken Changed => _token;
        public override void EnsureCurrent()
        {
            if (Volatile.Read(ref _disposed) != 0 || _live.IsInvalidated || _token.IsCancellationRequested) throw SessionRowSources.Changed();
            _check();
        }
        public override void Watch() => throw SessionRowSources.Changed(); // Never starts the unqualified copy follower.
        public override string? ActiveRun => null;
        private ExpectedSource OwnerSource => new(_live.Machine, 0, null);
        public override async Task<JsonElement> PageAsync(long before, int limit, CancellationToken ct)
        {
            EnsureCurrent();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, Changed);
            var args = new RowsFeature.PageArgs(_session, before, limit, OwnerSource);
            var page = await _machine.CallAsync("rows", "page", JsonSerializer.SerializeToElement(args, RowJsonContext.Rows.PageArgs),
                true, stop.Token, _live.Machine, expectedOwner: _live.OwnerBinding, expectedMirror: _live);
            EnsureCurrent(); return page;
        }
        public override async Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken ct)
        {
            EnsureCurrent();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, Changed);
            var args = new RowsFeature.SubagentArgs(_session, after, limit, OwnerSource);
            var value = await _machine.CallAsync("rows", "subagents", JsonSerializer.SerializeToElement(args, RowJsonContext.Rows.SubagentArgs),
                true, stop.Token, _live.Machine, expectedOwner: _live.OwnerBinding, expectedMirror: _live);
            EnsureCurrent();
            return value.Deserialize(RowJsonContext.Rows.SubagentPage) ?? throw new CallFailed("Invalid subagent page.");
        }
        public override async IAsyncEnumerable<JsonElement> WatchAsync(long? after, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureCurrent();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, Changed);
            await foreach (var frame in _machine.BoundRowsAsync(_session, after, _live.OwnerBinding, _live, stop.Token))
            {
                EnsureCurrent(); yield return frame;
            }
        }
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _changed.Dispose();
        }
    }

    private static CallFailed Changed() => new("The selected row source changed.", "source_changed");
}
