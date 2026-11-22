using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.Files;

/// <summary>A path target whose machine is a saved machine's name goes to that machine's backend over its files relay,
/// and that backend runs its own path rule. Two real backends in one process, so both see the same disk: which one
/// answered shows in the owner's engine id and in the calls the remote's files feature ran.</summary>
public sealed class FileRemotePathTests : IAsyncLifetime
{
    private const string Remote = "workbox-test";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly TwoHosts _hosts = new(forwardCommands: true);
    private readonly BackgroundWork _macWork = new(TimeProvider.System, TextWriter.Null);
    private readonly BackgroundWork _remoteWork = new(TimeProvider.System, TextWriter.Null);
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("files-remote-path-");
    private readonly ConcurrentDictionary<string, int> _remoteCalls = new();
    private FileOwner _macOwners = null!;
    private FileRemoteRoutes _routes = null!;
    private Func<FileOwnership>? _olderOwner = null;
    private Func<string, Task>? _remoteWriting;
    private readonly TaskCompletionSource _scopeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _profileCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseProfile = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable _beforeConnections = null!;

    private string Target => Path.Combine(_folder.FullName, "a.txt");

    public async Task InitializeAsync()
    {
        File.WriteAllText(Target, "old");
        // Subscribed before the connection owner starts, so this runs first: a changed profile is committed while the
        // old connection is still the active one, the only time the owner fence itself can refuse.
        _beforeConnections = _hosts.Mac.Topics.Subscribe("saved-machines", null, _ => { }, (_, json) =>
        {
            if (!json.Contains("changed-profile", StringComparison.Ordinal)) return;
            _profileCommitted.TrySetResult(); _releaseProfile.Task.GetAwaiter().GetResult();
        });
        ICallRegistry? macCalls = null;
        _hosts.Mac.AdditionalFeatures = calls => macCalls = calls;
        _hosts.Remote.AdditionalFeatures = calls => new FilesFeature(_hosts.Remote.Sessions,
            new FileOwner(_hosts.Remote.Sessions, _hosts.Remote.Fixture.Db), _remoteWork, sources: new SessionRowSources(_hosts.Remote.Fixture.Db,
                _hosts.Remote.Sessions, new MachineSet(new FakeMachine(null, _hosts.Remote.Sessions)))).Register(new RemoteFiles(calls, this));
        await _hosts.Start();
        await _hosts.StartMirror();
        _macOwners = new FileOwner(_hosts.Mac.Sessions, _hosts.Mac.Fixture.Db);
        _routes = new FileRemoteRoutes(_hosts.Mac.Fixture.Db, _macOwners, _hosts.Connections, _macWork);
        new FilesFeature(_hosts.Mac.Sessions, _macOwners, _macWork, _routes).Register(macCalls!);
    }

    public async Task DisposeAsync()
    {
        _releaseProfile.TrySetResult();
        _beforeConnections.Dispose();
        await _hosts.DisposeAsync();
        await _macWork.DisposeAsync();
        await _remoteWork.DisposeAsync();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task A_path_target_on_a_remote_reads_the_remote_s_file()
    {
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        var owner = Result(await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"}}"""));
        Assert.Equal(await RemoteEngine(), owner.GetProperty("machine").GetString());
        Assert.Equal(_folder.FullName, owner.GetProperty("checkoutRoot").GetString());
        Assert.Equal("a.txt", owner.GetProperty("file").GetString());
        Assert.Equal("", owner.GetProperty("remoteSessionId").GetString());
        var read = Result(await CallAsync(bulk, 2, "read",
            $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"},"expectedOwner":{{{owner.GetRawText()}}}}"""));
        Assert.Equal("old", read.GetProperty("content").GetString());
        Assert.Equal(1, Calls("owner"));
        Assert.Equal(1, Calls("read"));
    }

    [Fact]
    public async Task A_changed_remote_engine_is_source_changed()
    {
        using var bulk = await OpenAsync(bulk: true);
        var stale = $$$"""{"machine":"an-older-engine","checkoutRoot":"{{{_folder.FullName}}}","remoteSessionId":"","file":"a.txt"}""";
        var answer = await CallAsync(bulk, 1, "read", $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"},"expectedOwner":{{{stale}}}}""");
        Assert.Equal("source_changed", Code(answer));
        Assert.Equal(0, Calls("read"));
    }

    [Fact]
    public async Task A_machine_that_isn_t_connected_says_so()
    {
        using var normal = await OpenAsync();
        var answer = await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}","machine":"elsewhere"}}""");
        Assert.Equal("unreachable", Code(answer));
        Assert.Equal("elsewhere: machine socket is disconnected", Message(answer));
    }

    [Fact]
    public async Task A_saved_machine_that_was_disconnected_says_so()
    {
        await _hosts.DisconnectMirror();
        using var normal = await OpenAsync();
        var answer = await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"}}""");
        Assert.Equal("unreachable", Code(answer));
        Assert.Equal("workbox-test: machine socket is disconnected", Message(answer));
    }

    [Fact]
    public async Task A_reconnect_to_the_same_engine_opens_a_new_route_and_the_next_call_works()
    {
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        var target = $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""";
        Result(await CallAsync(normal, 1, "owner", $$"""{"target":{{target}}}"""));
        Result(await CallAsync(bulk, 2, "read", $$"""{"target":{{target}}}"""));
        var scopes = Calls("openScope");
        await ReconnectAsync();
        Assert.Equal("a.txt", Result(await CallAsync(normal, 3, "owner", $$"""{"target":{{target}}}""")).GetProperty("file").GetString());
        Assert.Equal("old", Result(await CallAsync(bulk, 4, "read", $$"""{"target":{{target}}}""")).GetProperty("content").GetString());
        Assert.True(Calls("openScope") > scopes);
    }

    [Fact]
    public async Task A_write_sent_before_a_reconnect_stays_uncertain_and_the_next_call_works()
    {
        using var bulk = await OpenAsync(bulk: true);
        var target = $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""";
        var upload = await StagedAsync(bulk, target, "new");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteWriting = async method =>
        {
            if (method != "commitSave") return;
            reached.TrySetResult();
            await release.Task;
        };
        try
        {
            var commit = CallAsync(bulk, 8, "commitSave", $$"""{"upload":"{{upload}}"}""");
            await reached.Task.WaitAsync(Wait);
            await ReconnectAsync();
            var answer = await commit;
            Assert.Equal("uncertain", Code(answer));
        }
        finally { release.TrySetResult(); }
        _remoteWriting = null;
        Assert.NotEqual(JsonValueKind.Null, Result(await CallAsync(bulk, 9, "read", $$"""{"target":{{target}}}""")).GetProperty("revision").ValueKind);
    }

    [Fact]
    public async Task A_second_target_call_during_a_staged_upload_leaves_the_upload_alive()
    {
        using var bulk = await OpenAsync(bulk: true);
        var target = $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""";
        var upload = await StagedAsync(bulk, target, "new");
        Assert.Equal("old", Result(await CallAsync(bulk, 8, "read", $$"""{"target":{{target}}}""")).GetProperty("content").GetString());
        Assert.Equal("saved", Result(await CallAsync(bulk, 9, "commitSave", $$"""{"upload":"{{upload}}"}""")).GetProperty("state").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(Target));
    }

    /// <summary>Call A captured its owner before a reconnect and reaches the routes after it, when call B has already opened
    /// the live route and begun an upload. A can't time that through the socket, so it goes to the route step directly.</summary>
    [Fact]
    public async Task A_call_holding_the_old_connection_is_refused_and_leaves_the_live_route_s_upload_alive()
    {
        var old = await _hosts.Connections.CaptureProjectOwnerAsync(Remote, default);
        await ReconnectAsync();
        var call = new CallContext(CancellationToken.None, "live", Peer: new OwnerPeer());
        var begun = await _routes.Invoke("beginSave",
            new BeginSave(null, null, await FileSave.RevisionAsync(Target, default), 3, Target: new PathTarget(Target, Remote)),
            FilesJson.Default.BeginSave, FilesJson.Default.BegunSave, call, bulk: true, write: true);
        Assert.True(begun.Forwarded);
        var upload = begun.Result.Upload;
        await _routes.Invoke("saveChunk", new SaveChunk(upload, 0, Encoding.UTF8.GetBytes("new")),
            FilesJson.Default.SaveChunk, FilesJson.Default.UploadAccepted, call, bulk: true, write: true);

        var refused = Assert.Throws<CallFailed>(() => _routes.TargetRoute("live", old, CancellationToken.None));
        Assert.Equal("project_owner_changed", refused.Reason);
        Assert.Equal("workbox-test's connection changed. Reopen the file.", refused.Message);

        var saved = await _routes.Invoke("commitSave", new CommitSave(upload),
            FilesJson.Default.CommitSave, FilesJson.Default.SaveResult, call, bulk: true, write: true);
        Assert.True(saved.Forwarded);
        Assert.Equal("saved", saved.Result.State);
        Assert.Equal("new", await File.ReadAllTextAsync(Target));
        _routes.Close("live");
    }

    [Fact]
    public async Task Closing_the_socket_closes_the_target_route_s_scope_on_the_remote()
    {
        var bulk = await OpenAsync(bulk: true);
        await StagedAsync(bulk, $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""", "new");
        Assert.NotEmpty(Stages());
        await bulk.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        bulk.Dispose();
        await _scopeClosed.Task.WaitAsync(Wait);
        Assert.Empty(Stages());
    }

    private string[] Stages() => Directory.GetFiles(_folder.FullName, ".assembly-save-*");

    /// <summary>A save on the remote that has its bytes and awaits its commit.</summary>
    private async Task<string> StagedAsync(WebSocket bulk, string target, string text)
    {
        var begun = Result(await CallAsync(bulk, 6, "beginSave",
            $$"""{"target":{{target}},"expectedRevision":"{{await FileSave.RevisionAsync(Target, default)}}","size":{{text.Length}}}"""));
        var upload = begun.GetProperty("upload").GetString()!;
        Result(await CallAsync(bulk, 7, "saveChunk",
            $$"""{"upload":"{{upload}}","offset":0,"data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}}"}"""));
        return upload;
    }

    /// <summary>Drops the remote's sockets; its machine connects again to the same engine, as a new owner connection.</summary>
    private async Task ReconnectAsync()
    {
        var before = await _hosts.Connections.CaptureProjectOwnerAsync(Remote, default);
        _hosts.Remote.BulkEndpoint.DropAll();
        _hosts.Remote.Endpoint.DropAll();
        _hosts.Remote.Commit(1);
        await _hosts.Reached(_hosts.Remote.Through);
        Assert.NotSame(before.Binding, (await _hosts.Connections.CaptureProjectOwnerAsync(Remote, default)).Binding);
    }

    private int Calls(string method) => _remoteCalls.GetValueOrDefault(method);

    private async Task<string> RemoteEngine() => (await _hosts.Connections.CaptureProjectOwnerAsync(Remote, default)).Engine;

    private static JsonElement Result(string answer)
    {
        var frame = JsonDocument.Parse(answer).RootElement;
        Assert.True(frame.TryGetProperty("result", out var result), answer);
        return result.Clone();
    }

    private static string? Code(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("code").GetString();
    private static string? Message(string answer) => JsonDocument.Parse(answer).RootElement.GetProperty("message").GetString();

    private async Task<ClientWebSocket> OpenAsync(bool bulk = false)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://" + _hosts.Mac.Address.Authority + (bulk ? "/socket/bulk" : "/socket")), CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","protocol":2,"token":"isolated-token"}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    /// <summary>Sends one call and answers its reply, skipping any other frame.</summary>
    private static async Task<string> CallAsync(WebSocket socket, long id, string method, string args)
    {
        await SendAsync(socket, $$$"""{"t":"call","id":{{{id}}},"feature":"files","method":"{{{method}}}","args":{{{args}}}}""");
        while (true)
        {
            var frame = await ReadAsync(socket);
            if (JsonDocument.Parse(frame).RootElement.TryGetProperty("id", out var reply) && reply.GetInt64() == id) return frame;
        }
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    /// <summary>The remote's files feature, counting the calls it ran; while <see cref="_olderOwner"/> is set, its
    /// <c>owner</c> answers as an older build's would.</summary>
    private sealed class RemoteFiles(ICallRegistry inner, FileRemotePathTests test) : ICallRegistry
    {
        public void Read<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<TResult>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, result, async (value, call) =>
            {
                test._remoteCalls.AddOrUpdate(method, 1, (_, count) => count + 1);
                if (method == "owner" && test._olderOwner is { } older) return (TResult)(object)older();
                var answer = await handler(value, call);
                if (method == "closeScope") test._scopeClosed.TrySetResult();
                return answer;
            }, bulk);

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) =>
            inner.Write(feature, method, args, result, async (value, call) =>
            {
                test._remoteCalls.AddOrUpdate(method, 1, (_, count) => count + 1);
                if (test._remoteWriting is { } writing) await writing(method);
                return await handler(value, call);
            }, order, failed, bulk);

        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
