using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.SourceControl;

internal sealed partial class SourceControlFeature
{
    // Registration remains on the one dispatcher: its queue, cancellation and bulk-channel rules still apply.
    internal ICallRegistry Route(ICallRegistry calls) => new GitRegistry(this, calls);

    private async Task<TResult> Invoke<TArgs, TResult>(ICallRegistry registry, string method, JsonTypeInfo<TArgs> argsType,
        JsonTypeInfo<TResult> resultType, TArgs args, CallContext call, CallKind kind, Func<Task<TResult>> local)
    {
        if (call.Peer is not OwnerPeer || GitCallKinds.KindOf(method) != kind || registry.KindOf("git", method) != kind ||
            kind != CallKind.Write && !MachineReads.AllowRead(registry, "git", method, kind == CallKind.BulkRead))
            throw new CallFailed("forbidden");
        var json = JsonSerializer.SerializeToElement(args, argsType);
        if (json.TryGetProperty("machine", out var machine) && machine.ValueKind != JsonValueKind.Null)
            throw new CallFailed("forbidden");
        if (!json.TryGetProperty("session", out var sessionValue) || sessionValue.ValueKind == JsonValueKind.Null)
            return await local(); // The existing pre-session project producers validate their own captured owner.
        if (sessionValue.ValueKind != JsonValueKind.String || sessionValue.GetString() is not { Length: > 0 } session)
            throw new CallBadArguments("A session is required.");
        var info = sessions.Get(session) ?? throw new CallFailed("Session not found");
        if (info.Machine is null) return await local();
        if (forward is null || connections is null) throw new CallFailed("This host does not own that session.");
        var target = await Capture(info.Machine, session, call.Cancellation);
        target.EnsureCurrent();
        if (method == "start" && json.TryGetProperty("operationId", out var operationId) && operationId.TryGetGuid(out var id))
            await operations.WaitRemoteObservationAsync(target.Owner.Engine, id, session,
                json.TryGetProperty("action", out var action) ? action.GetString() : null, call.Cancellation);
        target.EnsureCurrent();
        JsonElement answer;
        try
        {
            answer = await forward.CallAsync(target.Owner.Engine, "git", method, json, kind == CallKind.BulkRead,
                call.Cancellation, target.Owner.Socket, target.Owner.Binding, target.Live, kind == CallKind.Write);
        }
        catch (MachineUnreachable error) when (kind == CallKind.Write && error.MayHaveBeenSent)
        {
            throw new CallUncertain(target.Owner.Engine);
        }
        try { target.EnsureCurrent(); }
        catch (Exception) when (kind == CallKind.Write) { throw new CallUncertain(target.Owner.Engine); }
        return answer.Deserialize(resultType) ?? throw new CallFailed("The owning machine returned an invalid Git result.");
    }

    private sealed record GitTarget(RemoteConnections.ProjectOwner Owner, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent);

    private async Task<GitTarget> Capture(string name, string? session, CancellationToken ct)
    {
        var matches = db.Query("SELECT machine,generation FROM engine_mirror WHERE name=$name AND state='live' LIMIT 2",
            row => new ExpectedSource(row.Text(0), row.Long(1), name), ct, new SqlArg("$name", name));
        if (matches.Count != 1) throw new CallFailed("The session's owning machine changed.", "source_changed");
        var source = await connections!.CaptureSessionOpenSourceAsync(db, matches[0], ct);
        var owner = await connections.CaptureProjectOwnerAsync(name, ct);
        if (owner.Engine != matches[0].Machine || !ReferenceEquals(owner.Machine, source.Machine) ||
            !ReferenceEquals(owner.Binding, source.Live.OwnerBinding))
            throw new CallFailed("The session's owning machine changed.", "source_changed");
        void EnsureCurrent()
        {
            connections!.RequireProjectOwner(owner, ct);
            source.EnsureCurrent();
            if (session is not null && sessions.Get(session)?.Machine != name)
                throw new CallFailed("The session's owning machine changed.", "source_changed");
        }
        EnsureCurrent();
        return new(owner, source.Live, EnsureCurrent);
    }

    internal IReadOnlyList<GitOperationStatus> ObserveRemoteOperation(string key, CallContext call, GitOperations operations)
    {
        if (call.Peer is not OwnerPeer || connections is null) throw new CallFailed("forbidden");
        var slash = key.IndexOf('/');
        if (slash <= 0 || !Guid.TryParseExact(key[(slash + 1)..], "D", out var id)) throw new CallFailed("forbidden");
        var machine = key[..slash];
        var names = db.Query("SELECT name FROM engine_mirror WHERE machine=$machine AND state='live' LIMIT 2",
            row => row.Text(0), call.Cancellation, new SqlArg("$machine", machine));
        if (names.Count != 1) throw new CallFailed("The owning machine is unavailable.", "source_changed");
        var initial = connections.ByEngineMachine(db, machine) ?? throw new CallFailed("The owning machine is unavailable.");
        var status = operations.ObserveRemote(machine, id, call.Connection, initial, async ct =>
        {
            var captured = await Capture(names[0], null, ct);
            if (captured.Owner.Engine != machine) throw new CallFailed("source_changed");
            return new(captured.Owner.Machine, captured.Owner.Socket, captured.Owner.Binding, captured.Live, captured.EnsureCurrent);
        });
        return status is null ? [] : [status];
    }

    private sealed class GitRegistry(SourceControlFeature owner, ICallRegistry inner) : ICallRegistry
    {
        public CallKind? KindOf(string feature, string method) => inner.KindOf(feature, method);
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false)
        {
            if (feature != "git") throw new InvalidOperationException("Expected the fixed Git feature.");
            inner.Read(feature, method, args, result, (value, call) => owner.Invoke(inner, method, args, result, value, call,
                bulk ? CallKind.BulkRead : CallKind.Read, () => handler(value, call)), bulk);
        }
        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false)
        {
            if (feature != "git" || bulk) throw new InvalidOperationException("Expected a fixed Git write.");
            inner.Write<TArgs, TResult>(feature, method, args, result, async (value, call) =>
            {
                Func<IWriteScope, TResult>? apply = null;
                var answer = await owner.Invoke(inner, method, args, result, value, call, CallKind.Write, async () =>
                {
                    apply = await handler(value, call);
                    return default!;
                });
                return apply ?? (_ => answer);
            }, order, failed, bulk);
        }
        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened, IRowSourceBindings sources) => inner.Rows(topic, state, opened, sources);
    }
}
