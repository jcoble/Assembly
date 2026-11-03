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

    public void Register(ICallRegistry calls) => calls.Read("sessions", "open_target", SessionOpenJson.Default.SessionOpenTarget,
        SessionOpenJson.Default.SessionOpenTargetRead, ResolveAsync);

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
        if (connections is null) throw Changed();
        var expected = db.QueryFirst("""
            SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
            """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), call.Cancellation,
            new SqlArg("$machine", target.Machine)) ?? throw Changed();
        var captured = await connections.CaptureSessionOpenSourceAsync(db, expected, call.Cancellation);
        var guardRemote = SessionRowSources.CapturedRemote(target.Session, captured.Machine, captured.Live, captured.EnsureCurrent);
        call.SourceGuard = guardRemote;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, guardRemote.Changed);
        captured.EnsureCurrent();
        // This is the actual owner's local validation, not a display-cache lookup under the saved name.
        JsonElement value;
        try
        {
            value = await captured.Machine.CallAsync("sessions", "open_target",
                JsonSerializer.SerializeToElement(target, SessionOpenJson.Default.SessionOpenTarget), false, stop.Token,
                expected.Machine, expectedOwner: captured.Live.OwnerBinding, expectedMirror: captured.Live);
        }
        catch (OperationCanceledException) when (!call.Cancellation.IsCancellationRequested && guardRemote.Changed.IsCancellationRequested)
        {
            throw Changed();
        }
        captured.EnsureCurrent();
        var resolved = value.Deserialize(SessionOpenJson.Default.SessionOpenTargetRead) ?? throw Changed();
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
        return resolved with { Generation = expected.Generation, RouteMachine = expected.RouteMachine };
    }

    private static CallFailed Changed() => new("The selected session source changed.", "source_changed");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionOpenTarget))]
[JsonSerializable(typeof(SessionOpenTargetRead))]
internal sealed partial class SessionOpenJson : JsonSerializerContext;
