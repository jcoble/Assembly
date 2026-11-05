using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Sessions;
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
        ValidateCheckoutArguments(method, json);
        if (json.TryGetProperty("projectId", out var project) && project.ValueKind != JsonValueKind.Null &&
            json.TryGetProperty("session", out var projectSession) && projectSession.ValueKind != JsonValueKind.Null)
            throw new CallBadArguments("Choose exactly one session or project.");
        if (json.TryGetProperty("machine", out var machine) && machine.ValueKind != JsonValueKind.Null)
            throw new CallFailed("forbidden");
        if (!json.TryGetProperty("session", out var sessionValue) || sessionValue.ValueKind == JsonValueKind.Null)
            return await local(); // The existing pre-session project producers validate their own captured owner.
        if (sessionValue.ValueKind != JsonValueKind.String || sessionValue.GetString() is not { Length: > 0 } session)
            throw new CallBadArguments("A session is required.");
        if (FolderView.IsView(session))
            return await InvokeView(method, resultType, json, FolderView.Parse(session), session, call, kind, local);
        var info = sessions.Get(session) ?? throw new CallFailed("unknown session");
        var qualified = kind == CallKind.Write || method is "checkouts" or "status" or "diff" or "branchDiff" or "branches" or "stashes" or "history" or "commitFiles" or "commitDiff" or "prs" or "pr" or "prFile" or "operation_host" or "operation";
        var assertedSource = json.TryGetProperty("expectedSource", out var asserted) && asserted.ValueKind != JsonValueKind.Null
            ? asserted.Deserialize(SourceControlJson.Default.ExpectedSource) : null;
        if (call.ExpectedSource is not null && assertedSource is not null && call.ExpectedSource != assertedSource)
            throw new CallFailed("The session's owning machine changed.", "source_changed");
        var expected = assertedSource ?? call.ExpectedSource;
        if (info.Machine is null)
        {
            if (expected is not null && (expected.Machine != operations.Machine || expected.Generation != 0 || expected.RouteMachine is not null))
                throw new CallFailed("The session's owning machine changed.", "source_changed");
            if (!qualified) return await local();
            if (kind == CallKind.Write && method is not ("cancel" or "release"))
            {
                var observedRoot = json.TryGetProperty("expectedCheckoutRoot", out var root) && root.ValueKind == JsonValueKind.String ? root.GetString() : null;
                SourceControlWrites.RequireRoot(sessions, session, observedRoot, expected is not null);
            }
            var selectedRoot = IsCheckoutRead(method) &&
                json.TryGetProperty("checkoutPath", out var selected) && selected.ValueKind != JsonValueKind.Null
                    ? RecordedCheckoutRoot(session) : null;
            void EnsureLocal()
            {
                if (sessions.Get(session) is not { Machine: null })
                    throw new CallFailed("The session's owning machine changed.", "source_changed");
                RequireLocalOwner(db, operations.Machine, session);
                // The sender can recheck recorded state, never filesystem/Git membership.
                if (selectedRoot is not null && RecordedCheckoutRoot(session) != selectedRoot) throw CheckoutRootChanged();
            }
            call.SourceGuard = new LocalReadSource(EnsureLocal);
            EnsureLocal();
            var answerLocal = await local();
            EnsureLocal();
            return answerLocal;
        }
        if (qualified && (expected is null || expected.RouteMachine != info.Machine))
            throw new CallFailed("The session's owning machine changed.", "source_changed");
        if (forward is null || connections is null) throw new CallFailed("This host does not own that session.");
        var target = await Capture(info.Machine, session, call.Cancellation, expected);
        if (qualified)
        {
            call.SourceGuard = SessionRowSources.CapturedRemote(session, target.Owner.Machine, target.Live, target.EnsureCurrent);
            json = OwnerArguments(json, target.Owner.Engine);
        }
        return await Forward(method, resultType, json, target, session, call, kind);
    }

    /// <summary>A viewed folder runs on its project's machine, with no session behind it to check.</summary>
    private async Task<TResult> InvokeView<TResult>(string method, JsonTypeInfo<TResult> resultType, JsonElement json, FolderView view,
        string session, CallContext call, CallKind kind, Func<Task<TResult>> local)
    {
        if (views.RemoteMachine(view) is { } machine)
        {
            if (forward is null || connections is null) throw new CallFailed("This host does not own that folder.");
            var target = await Capture(machine, null, call.Cancellation);
            call.SourceGuard = SessionRowSources.CapturedRemote(session, target.Owner.Machine, target.Live, target.EnsureCurrent);
            return await Forward(method, resultType, OwnerArguments(json, target.Owner.Engine), target, session, call, kind);
        }
        var folder = view.LocalFolder();
        if (kind == CallKind.Write && method is not ("cancel" or "release"))
        {
            views.RequireWritable(folder, call.Cancellation);
            var observed = json.TryGetProperty("expectedCheckoutRoot", out var root) && root.ValueKind == JsonValueKind.String ? root.GetString() : null;
            SourceControlWrites.RequireRoot(sessions, session, observed, required: false);
        }
        call.SourceGuard = new LocalReadSource(() => view.LocalFolder());
        return await local();
    }

    private async Task<TResult> Forward<TResult>(string method, JsonTypeInfo<TResult> resultType, JsonElement json, GitTarget target,
        string session, CallContext call, CallKind kind)
    {
        using var sourceStop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation,
            call.SourceGuard?.Changed ?? CancellationToken.None);
        target.EnsureCurrent();
        if (method == "start" && json.TryGetProperty("operationId", out var operationId) && operationId.TryGetGuid(out var id))
            await operations.WaitRemoteObservationAsync(target.Owner.Engine, id, session,
                json.TryGetProperty("action", out var action) ? action.GetString() : null, call.Cancellation);
        target.EnsureCurrent();
        JsonElement answer;
        try
        {
            answer = await forward!.CallAsync(target.Owner.Engine, "git", method, json, kind == CallKind.BulkRead,
                sourceStop.Token, target.Owner.Socket, target.Owner.Binding, target.Live, kind == CallKind.Write);
        }
        catch (MachineUnreachable error) when (kind == CallKind.Write && error.MayHaveBeenSent)
        {
            throw new CallUncertain(target.Owner.Engine);
        }
        var result = answer.Deserialize(resultType) ?? throw new CallFailed("The owning machine returned an invalid Git result.");
        if (result is GitOperationStatus status && status.Machine == target.Owner.Engine &&
            json.TryGetProperty("operationId", out var expectedId) && expectedId.TryGetGuid(out var expectedOperationId) && expectedOperationId == status.OperationId)
            result = (TResult)(object)operations.RecordRemoteStatus(status);
        try { target.EnsureCurrent(); }
        catch (Exception) when (kind == CallKind.Write) { throw new CallUncertain(target.Owner.Engine); }
        return result;
    }

    internal static void RequireLocalOwner(IAppDatabase database, string machine, string session)
    {
        if (database.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0), default) != machine ||
            database.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", row => row.Text(0), default,
                new SqlArg("$id", session)) is { } owner && owner != machine)
            throw new CallFailed("The session's owning machine changed.", "source_changed");
    }

    private sealed class LocalReadSource(Action ensureCurrent) : IExpectedSourceGuard
    {
        public CancellationToken Changed => CancellationToken.None;
        public void EnsureCurrent() => ensureCurrent();
        public void Dispose() { }
    }

    private static JsonElement OwnerArguments(JsonElement args, string machine)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in args.EnumerateObject())
                if (property.Name != "expectedSource") property.WriteTo(writer);
            writer.WritePropertyName("expectedSource");
            JsonSerializer.Serialize(writer, new ExpectedSource(machine, 0, null), SourceControlJson.Default.ExpectedSource);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    private sealed record GitTarget(RemoteConnections.ProjectOwner Owner, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent);

    private async Task<GitTarget> Capture(string name, string? session, CancellationToken ct, ExpectedSource? expected = null)
    {
        var matches = db.Query("SELECT machine,generation FROM engine_mirror WHERE name=$name AND state='live' LIMIT 2",
            row => new ExpectedSource(row.Text(0), row.Long(1), name), ct, new SqlArg("$name", name));
        if (matches.Count != 1 || expected is not null && matches[0] != expected) throw new CallFailed("The session's owning machine changed.", "source_changed");
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
