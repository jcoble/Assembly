using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Files uses the existing verified owner transport, with one resource scope per incoming socket and owner.</summary>
internal sealed class FileRemoteRoutes(IAppDatabase db, FileOwner owners, RemoteConnections connections, BackgroundWork work)
{
    private sealed class Route
    {
        internal readonly SemaphoreSlim Gate = new(1);
        internal RemoteMachine.FileRelayChannel? Channel;
        internal Action? EnsureCurrent;
        internal ExpectedSource? Source;
        internal volatile bool Closed;
    }
    private readonly object gate = new();
    private readonly Dictionary<(string Connection, string Machine), Route> routes = [];
    private readonly Dictionary<(string Connection, string Kind, string Id), Route> resources = [];
    private readonly record struct ReadKey(string Connection, string Session, string Path, string Revision);
    private readonly Dictionary<ReadKey, Route> readRoutes = [];

    internal string? Order<T>(T args, JsonTypeInfo<T> type)
    {
        var json = JsonSerializer.SerializeToElement(args, type);
        if (HasTarget(json, out var machine))
            return machine is null ? null : "files-path:" + machine + ":" + json.GetProperty("target").GetProperty("path").GetString();
        if (!json.TryGetProperty("session", out var session) || session.GetString() is not { } id) return null;
        // Queue ordering is not authority. Qualified calls validate the supplied source before any owner lookup.
        if (!json.TryGetProperty("expectedSource", out var source) || source.ValueKind == JsonValueKind.Null) return null;
        return "files-source:" + source.GetProperty("machine").GetString() + ":" + id;
    }

    internal async Task<(bool Forwarded, TResult Result)> Invoke<TArgs, TResult>(string method, TArgs args,
        JsonTypeInfo<TArgs> input, JsonTypeInfo<TResult> output, CallContext call, bool bulk, bool write)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
        var json = JsonSerializer.SerializeToElement(args, input);
        if (json.TryGetProperty("relayScope", out var relayed) && relayed.ValueKind != JsonValueKind.Null)
            return (false, default!); // An admitted remote scope executes only this server's existing local producer.
        if (HasTarget(json, out var targetMachine) && targetMachine is null) return (false, default!); // This machine's file.
        var expected = json.TryGetProperty("expectedSource", out var asserted) && asserted.ValueKind != JsonValueKind.Null
            ? asserted.Deserialize(FilesJson.Default.ExpectedSource) : null;
        var expectedOwner = json.TryGetProperty("expectedOwner", out var ownerValue) && ownerValue.ValueKind != JsonValueKind.Null
            ? ownerValue.Deserialize(FilesJson.Default.FileOwnership) : null;
        Route? route;
        FileOwnership? ownership = null;
        string? sessionId = null;
        if (method == "release")
        {
            var key = ReadIdentity(call.Connection, json, json.GetProperty("revision").GetString()!);
            lock (gate) readRoutes.TryGetValue(key, out route);
            if (route is null) return (false, default!); // Local snapshots retain their existing root-independent release.
        }
        else if (json.TryGetProperty("session", out var session))
        {
            sessionId = session.GetString() ?? throw new CallBadArguments("A file session is required.");
            ownership = owners.Resolve(sessionId, call.Cancellation, expectedOwner);
            if (owners.IsLocal(ownership, call.Cancellation)) return (false, default!);
            if (expected is null || expected.Machine != ownership.Machine || expected.Generation <= 0 ||
                expected.RouteMachine is not { Length: > 0 })
                throw new CallFailed("Reopen this file from its actual source before using remote files.", "source_changed");
            if (method == "owner")
            {
                var captured = await Capture(ownership, expected, call.Cancellation);
                if (owners.Resolve(sessionId, call.Cancellation) != ownership) throw new CallFailed("source_changed");
                captured.EnsureCurrent();
                return (true, JsonSerializer.SerializeToElement(ownership, FilesJson.Default.FileOwnership).Deserialize(output)!);
            }
            lock (gate)
            {
                var key = (call.Connection, ownership.Machine);
                if (!routes.TryGetValue(key, out route)) routes.Add(key, route = new Route());
            }
        }
        else
        {
            var resource = Resource(method, json);
            if (resource is null) return (false, default!);
            lock (gate) resources.TryGetValue((call.Connection, resource.Value.Kind, resource.Value.Id), out route);
            if (route is null) return (false, default!);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, call.Closed);
        var ct = stop.Token;
        await route!.Gate.WaitAsync(ct);
        try
        {
            if (route.Closed) throw new CallFailed("The file connection closed.", "files_scope_changed");
            if (route.Source is not null && expected is not null && route.Source != expected)
                throw new CallFailed("The file source changed. Reopen the file.", "source_changed");
            if (route.Channel is null)
            {
                if (ownership is null) throw new CallFailed("Unknown remote file resource.");
                var captured = await Capture(ownership, expected!, ct);
                route.Channel = await captured.Owner.Machine.OpenFilesAsync(captured.Owner.Binding, captured.Live, ct);
                route.EnsureCurrent = captured.EnsureCurrent;
                route.Source = expected;
                if (route.Closed || call.Closed.IsCancellationRequested)
                {
                    CloseChannel(route);
                    throw new OperationCanceledException(call.Closed);
                }
            }
            var ensureCurrent = route.EnsureCurrent
                ?? throw new CallFailed("The file source binding is unavailable. Reopen the file.", "source_changed");
            if (method != "release") ensureCurrent();
            if (ownership is not null && owners.Resolve(sessionId!, ct) != ownership)
                throw new CallFailed("The file owner or checkout changed.", "source_changed");
            var scoped = WithScope(args, route.Channel.Scope, new ExpectedSource(route.Channel.Machine, 0, null));
            JsonElement answer;
            try
            {
                answer = await route.Channel.CallAsync(method, JsonSerializer.SerializeToElement(scoped, input), bulk, ct);
                if (method != "release") ensureCurrent();
                if (ownership is not null && owners.Resolve(sessionId!, ct) != ownership)
                    throw new CallFailed("The file owner or checkout changed.", "source_changed");
            }
            catch (Exception error) when (write && (error is MachineUnreachable or IOException or OperationCanceledException or
                System.Net.WebSockets.WebSocketException or ObjectDisposedException ||
                error is CallFailed failed && failed.Reason is "source_changed" or "files_scope_changed" or "project_owner_changed"))
            {
                Forget(route);
                throw new CallUncertain(route.Channel.Machine, "The file operation may have completed. Inspect the owning checkout before trying again.");
            }
            lock (gate)
            {
                if (method == "read")
                {
                    var key = ReadIdentity(call.Connection, json, answer.GetProperty("revision").GetString()!);
                    foreach (var old in readRoutes.Where(pair => ReferenceEquals(pair.Value, route) &&
                        pair.Key.Connection == key.Connection && pair.Key.Session == key.Session && pair.Key.Path == key.Path)
                        .Select(pair => pair.Key).ToArray()) readRoutes.Remove(old);
                    if (answer.GetProperty("nextOffset").ValueKind != JsonValueKind.Null) readRoutes[key] = route;
                }
                if (method == "release") readRoutes.Remove(ReadIdentity(call.Connection, json, json.GetProperty("revision").GetString()!));
                if (method == "beginSave") resources[(call.Connection, "upload", answer.GetProperty("upload").GetString()!)] = route;
                if (method == "observe") resources[(call.Connection, "lease", answer.GetProperty("lease").GetString()!)] = route;
                if (method == "search" && answer.GetProperty("more").GetBoolean())
                    resources[(call.Connection, "searchId", answer.GetProperty("searchId").GetString()!)] = route;
                if (method == "search" && !answer.GetProperty("more").GetBoolean())
                    resources.Remove((call.Connection, "searchId", answer.GetProperty("searchId").GetString()!));
                var resource = Resource(method, json);
                if (resource is { } ended && method is "commitSave" or "dropSave" or "unobserve" or "releaseSearch")
                    resources.Remove((call.Connection, ended.Kind, ended.Id));
            }
            return (true, answer.Deserialize(output) ?? throw new CallFailed("The owning machine returned an invalid file result."));
        }
        catch (Exception error) when (error is MachineUnreachable or IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException ||
            error is CallFailed failed && failed.Reason is "source_changed" or "files_scope_changed" or "project_owner_changed")
        {
            Forget(route);
            throw;
        }
        catch (OperationCanceledException)
        {
            // A cancelled creation may have a successful late reply whose resource ID was not delivered.
            // End this exact scope so that no unseen upload, watch, search or read snapshot is retained.
            Forget(route);
            throw;
        }
        finally { route.Gate.Release(); }
    }

    private sealed record Target(RemoteConnections.ProjectOwner Owner, EngineMirror.LiveMirrorBinding Live, Action EnsureCurrent);
    private async Task<Target> Capture(FileOwnership ownership, ExpectedSource expected, CancellationToken ct)
    {
        if (expected.Machine != ownership.Machine || expected.RouteMachine is not { Length: > 0 })
            throw new CallFailed("The file owner changed.", "source_changed");
        var source = await connections.CaptureSessionOpenSourceAsync(db, expected, ct);
        var owner = await connections.CaptureProjectOwnerAsync(expected.RouteMachine, ct);
        if (owner.Engine != ownership.Machine || !ReferenceEquals(owner.Machine, source.Machine) ||
            !ReferenceEquals(owner.Binding, source.Live.OwnerBinding)) throw new CallFailed("The file owner changed.", "source_changed");
        void EnsureCurrent()
        {
            connections.RequireProjectOwner(owner, CancellationToken.None);
            source.EnsureCurrent();
        }
        EnsureCurrent();
        return new(owner, source.Live, EnsureCurrent);
    }

    internal void Close(string connection)
    {
        Route[] ending;
        lock (gate)
        {
            ending = routes.Where(pair => pair.Key.Connection == connection).Select(pair => pair.Value).ToArray();
            foreach (var key in routes.Keys.Where(key => key.Connection == connection).ToArray()) routes.Remove(key);
            foreach (var key in resources.Keys.Where(key => key.Connection == connection).ToArray()) resources.Remove(key);
            foreach (var key in readRoutes.Keys.Where(key => key.Connection == connection).ToArray()) readRoutes.Remove(key);
            foreach (var route in ending) route.Closed = true;
        }
        foreach (var route in ending) work.Start("remote file scope close", async ct =>
        {
            await route.Gate.WaitAsync(ct);
            try { if (route.Channel is not null) await route.Channel.CloseAsync(ct); }
            finally { route.Gate.Release(); }
        });
    }

    private void CloseChannel(Route route)
    {
        route.Closed = true;
        work.Start("remote file cancelled scope", ct => route.Channel!.CloseAsync(ct));
    }

    private void Forget(Route route)
    {
        lock (gate)
        {
            foreach (var key in routes.Where(pair => ReferenceEquals(pair.Value, route)).Select(pair => pair.Key).ToArray()) routes.Remove(key);
            foreach (var key in resources.Where(pair => ReferenceEquals(pair.Value, route)).Select(pair => pair.Key).ToArray()) resources.Remove(key);
            foreach (var key in readRoutes.Where(pair => ReferenceEquals(pair.Value, route)).Select(pair => pair.Key).ToArray()) readRoutes.Remove(key);
            route.Closed = true;
        }
        if (route.Channel is not null) CloseChannel(route);
    }

    private static bool HasTarget(JsonElement args, out string? machine)
    {
        machine = null;
        if (!args.TryGetProperty("target", out var target) || target.ValueKind == JsonValueKind.Null) return false;
        machine = target.GetProperty("machine").GetString();
        return true;
    }

    private static (string Kind, string Id)? Resource(string method, JsonElement args)
    {
        var kind = method switch
        {
            "saveChunk" or "commitSave" or "dropSave" or "compare" => "upload",
            "unobserve" => "lease",
            "releaseSearch" => "searchId",
            _ => null
        };
        return kind is not null && args.TryGetProperty(kind, out var id) && id.GetString() is { } value ? (kind, value) : null;
    }

    private static ReadKey ReadIdentity(string connection, JsonElement args, string revision) =>
        new(connection, args.GetProperty("session").GetString()!, args.GetProperty("path").GetString()!, revision);

    private static T WithScope<T>(T args, string scope, ExpectedSource source)
    {
        object scoped = args switch
        {
            ObserveRoot a => a with { RelayScope = scope, ExpectedSource = source },
            UnobserveRoot a => a with { RelayScope = scope, ExpectedSource = source },
            FilesFeature.ListArgs a => a with { RelayScope = scope, ExpectedSource = source },
            SearchArgs a => a with { RelayScope = scope, ExpectedSource = source },
            ReleaseSearch a => a with { RelayScope = scope, ExpectedSource = source },
            FilesFeature.ReadArgs a => a with { RelayScope = scope, ExpectedSource = source },
            FilesFeature.ReleaseArgs a => a with { RelayScope = scope, ExpectedSource = source },
            BeginSave a => a with { RelayScope = scope, ExpectedSource = source },
            SaveChunk a => a with { RelayScope = scope, ExpectedSource = source },
            CommitSave a => a with { RelayScope = scope, ExpectedSource = source },
            DropSave a => a with { RelayScope = scope, ExpectedSource = source },
            CompareFile a => a with { RelayScope = scope, ExpectedSource = source },
            FileActionArgs a => a with { RelayScope = scope, ExpectedSource = source },
            _ => throw new CallFailed("Unknown remote file operation.")
        };
        return (T)scoped;
    }
}
