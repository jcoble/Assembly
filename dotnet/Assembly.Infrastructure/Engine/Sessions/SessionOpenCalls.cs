using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Sessions;

/// <summary>Resolves a display target on its actual owner. Reading history never admits operational work.</summary>
internal sealed class SessionOpenCalls(IAppDatabase db, EngineRuntime runtime, SessionStore sessions, MachineSet machines,
    RemoteConnections? connections = null) : IFeature
{
    private readonly SessionOpenTargets _targets = new(db, sessions);
    private readonly SessionRowSources _sources = new(db, sessions, machines, connections);

    public void Register(ICallRegistry calls)
    {
        calls.Read("sessions", "open_target", SessionOpenJson.Default.SessionOpenTarget,
            SessionOpenJson.Default.SessionOpenTargetRead, ResolveAsync);
        calls.Read("sessions", "resolve_source", SessionOpenJson.Default.ResolveSessionSourceArgs,
            SessionOpenJson.Default.ExpectedSource, new SessionOpenSources(db, runtime).ResolveAsync);
    }

    private async Task<SessionOpenTargetRead> ResolveAsync(SessionOpenTarget target, CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        call.Cancellation.ThrowIfCancellationRequested();
        if (target.Machine == runtime.Machine)
        {
            var source = new ExpectedSource(runtime.Machine, 0, null);
            var read = _targets.Validate(target, source, call.Cancellation);
            var guard = await _sources.CaptureAsync(source, read.SessionId, call.Cancellation);
            call.SourceGuard = guard;
            guard.EnsureCurrent(); return read;
        }
        // A session saved here opens from its copy, connected or not (TSK-1692).
        if (_targets.ValidateCopy(target, call.Cancellation) is { } copied)
        {
            var guard = await _sources.CaptureAsync(copied.ExpectedSource, copied.SessionId, call.Cancellation);
            call.SourceGuard = guard;
            if (target.Kind == "engine") copied = copied with { Draft = await RemoteDraftAsync(target, copied.RouteMachine!, call.Cancellation) };
            guard.EnsureCurrent(); return copied;
        }
        if (connections is null) throw Changed();
        var name = db.QueryFirst("SELECT name FROM engine_mirror WHERE machine=$machine AND state<>'retired'", row => row.Text(0),
            call.Cancellation, new SqlArg("$machine", target.Machine)) ?? throw Changed();
        if (!await connections.LiveAsync(name, call.Cancellation)) throw CopyGuard.NotSaved(name);
        var (resolved, machine, stop) = await ResolveOnOwnerAsync(target, call);
        using (stop)
        {
            // Never saved here: its newest screen is downloaded before the open returns, so it reads from the copy.
            await machine.EnsureCopyAsync(resolved.SessionId, stop.Token);
            return resolved;
        }
    }

    // The actual owner's own validation of the target, under its live mirror; also records the session's facade here.
    private async Task<(SessionOpenTargetRead Read, RemoteMachine Machine, CancellationTokenSource Stop)> ResolveOnOwnerAsync(
        SessionOpenTarget target, CallContext call)
    {
        var expected = db.QueryFirst("""
            SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
            """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), call.Cancellation,
            new SqlArg("$machine", target.Machine)) ?? throw Changed();
        var captured = await connections!.CaptureSessionOpenSourceAsync(db, expected, call.Cancellation);
        var guardRemote = SessionRowSources.CapturedRemote(captured.Machine, captured.Live, captured.EnsureCurrent);
        call.SourceGuard = guardRemote;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, guardRemote.Changed);
        try
        {
            captured.EnsureCurrent();
            // A session this desktop didn't create gets its facade from the live mirror first, so what the open returns
            // can be read, saved and sent to (helpers, workflow and phone sessions, other desktops' sessions).
            connections.RecordOpenedFacade(sessions, captured.Machine, captured.Live, captured.EnsureCurrent, target.Session, call.Cancellation);
            var resolved = await OwnerTargetAsync(target, captured.Machine, captured.Live, expected, stop.Token);
            if (resolved.Kind != target.Kind || resolved.Machine != expected.Machine || resolved.Generation != 0 ||
                resolved.RouteMachine is not null || resolved.OriginalKey != new SessionOpenKey(target.Machine, target.Session) ||
                resolved.RequestId != target.RequestId || string.IsNullOrWhiteSpace(resolved.SessionId)) throw Changed();
            if (target.Kind == "native")
            {
                if (resolved.ParentSessionId != target.Session || resolved.ItemId != target.ItemId || resolved.Seq != target.Seq ||
                    resolved.PayloadSessionId != target.PayloadSessionId || !resolved.ReadOnly || !resolved.HasHistoryRows)
                    throw Changed();
            }
            else if (resolved.SessionId != target.Session || resolved.ReadOnly != (target.Kind == "historical")) throw Changed();
            guardRemote.EnsureCurrent();
            return (resolved with { Generation = expected.Generation, RouteMachine = expected.RouteMachine }, captured.Machine, stop);
        }
        catch (OperationCanceledException) when (!call.Cancellation.IsCancellationRequested && guardRemote.Changed.IsCancellationRequested)
        {
            stop.Dispose();
            throw Changed();
        }
        catch
        {
            stop.Dispose();
            throw;
        }
    }

    // The remote's own open_target. This is the actual owner's local validation, not a display-cache lookup.
    private static async Task<SessionOpenTargetRead> OwnerTargetAsync(SessionOpenTarget target, RemoteMachine machine,
        EngineMirror.LiveMirrorBinding live, ExpectedSource expected, CancellationToken ct)
    {
        var value = await machine.CallAsync("sessions", "open_target",
            JsonSerializer.SerializeToElement(target, SessionOpenJson.Default.SessionOpenTarget), false, ct,
            expected.Machine, expectedOwner: live.OwnerBinding, expectedMirror: live);
        return value.Deserialize(SessionOpenJson.Default.SessionOpenTargetRead) ?? throw Changed();
    }

    // A copied engine session's draft is saved on its machine: asked for with the open while connected, on the open's
    // token. Offline, or when the ask fails, the box opens empty.
    private async Task<Assembly.Application.Features.Chat.Sessions.Drafts.SessionDraft?> RemoteDraftAsync(SessionOpenTarget target,
        string route, CancellationToken ct)
    {
        if (connections is null || !await connections.LiveAsync(route, ct)) return null;
        try
        {
            var expected = db.QueryFirst("""
                SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
                """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), ct, new SqlArg("$machine", target.Machine));
            if (expected is null) return null;
            var captured = await connections.CaptureSessionOpenSourceAsync(db, expected, ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, captured.Live.Invalidated);
            return (await OwnerTargetAsync(target, captured.Machine, captured.Live, expected, stop.Token)).Draft;
        }
        catch (Exception error) when (error is CallFailed or MachineUnreachable or IOException or System.Net.WebSockets.WebSocketException
            or JsonException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static CallFailed Changed() => new("The selected session source changed.", "source_changed");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionOpenTarget))]
[JsonSerializable(typeof(SessionOpenTargetRead))]
[JsonSerializable(typeof(ResolveSessionSourceArgs))]
[JsonSerializable(typeof(ExpectedSource))]
internal sealed partial class SessionOpenJson : JsonSerializerContext;
