using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
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
    private Func<FileOwnership>? _olderOwner;
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
            new FileOwner(_hosts.Remote.Sessions, _hosts.Remote.Fixture.Db), _remoteWork).Register(new RemoteFiles(calls, this));
        await _hosts.Start();
        await _hosts.StartMirror();
        _macOwners = new FileOwner(_hosts.Mac.Sessions, _hosts.Mac.Fixture.Db);
        new FilesFeature(_hosts.Mac.Sessions, _macOwners, _macWork,
            new FileRemoteRoutes(_hosts.Mac.Fixture.Db, _macOwners, _hosts.Connections, _macWork)).Register(macCalls!);
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
    public async Task A_path_target_on_a_remote_saves_there()
    {
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        var target = $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""";
        var owner = Result(await CallAsync(normal, 1, "owner", $$"""{"target":{{target}}}"""));
        var begun = Result(await CallAsync(bulk, 2, "beginSave",
            $$"""{"target":{{target}},"expectedOwner":{{owner.GetRawText()}},"expectedRevision":"{{await FileSave.RevisionAsync(Target, default)}}","size":3}"""));
        var upload = begun.GetProperty("upload").GetString();
        Result(await CallAsync(bulk, 3, "saveChunk",
            $$"""{"upload":"{{upload}}","offset":0,"data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("new"))}}"}"""));
        Assert.Equal("saved", Result(await CallAsync(bulk, 4, "commitSave", $$"""{"upload":"{{upload}}"}""")).GetProperty("state").GetString());
        Assert.Equal("new", await File.ReadAllTextAsync(Target));
        Assert.Equal(1, Calls("beginSave"));
        Assert.Equal(1, Calls("commitSave"));
    }

    [Fact]
    public async Task Release_by_path_target_is_forwarded_to_the_remote()
    {
        await File.WriteAllTextAsync(Target, new string('x', FileRead.ChunkBytes + 1));
        using var normal = await OpenAsync();
        using var bulk = await OpenAsync(bulk: true);
        var target = $$$"""{"path":"{{{Target}}}","machine":"{{{Remote}}}"}""";
        var owner = Result(await CallAsync(normal, 1, "owner", $$"""{"target":{{target}}}"""));
        var read = Result(await CallAsync(bulk, 2, "read", $$"""{"target":{{target}},"expectedOwner":{{owner.GetRawText()}}}"""));
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("nextOffset").ValueKind);
        var released = Result(await CallAsync(bulk, 3, "release",
            $$"""{"target":{{target}},"revision":"{{read.GetProperty("revision").GetString()}}","expectedOwner":{{owner.GetRawText()}}}"""));
        Assert.True(released.GetProperty("released").GetBoolean());
        Assert.Equal(1, Calls("release"));
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
    public async Task A_save_whose_machine_went_away_never_speaks_of_projects()
    {
        using var bulk = await OpenAsync(bulk: true);
        var begun = Result(await CallAsync(bulk, 1, "beginSave",
            $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"},"expectedRevision":"{{{await FileSave.RevisionAsync(Target, default)}}}","size":3}"""));
        await _hosts.DisconnectMirror();
        var answer = await CallAsync(bulk, 2, "saveChunk",
            $$$"""{"upload":"{{{begun.GetProperty("upload").GetString()}}}","offset":0,"data":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("new"))}}}"}""");
        Assert.Equal("project_owner_changed", Code(answer));
        Assert.Equal("workbox-test's connection changed. Reopen the file.", Message(answer));
        Assert.Equal("old", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task A_changed_machine_profile_says_not_connected_and_never_speaks_of_projects()
    {
        using var normal = await OpenAsync();
        var profile = (await _hosts.Connections.CaptureProjectOwnerAsync(Remote, default)).Profile;
        var update = Task.Run(() => new MachineStore(_hosts.Mac.Fixture.Db).Import(profile with { Ssh = "changed-profile" }));
        try
        {
            await _profileCommitted.Task.WaitAsync(Wait);
            var answer = await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"}}""");
            Assert.Equal("unreachable", Code(answer));
            Assert.Equal("workbox-test: machine socket is disconnected", Message(answer));
        }
        finally { _releaseProfile.TrySetResult(); }
        await update.WaitAsync(Wait);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_older_remote_says_it_is_older(bool refuses)
    {
        _olderOwner = refuses
            ? () => throw new CallBadArguments("files/owner: JSON deserialization for type 'ObserveRoot' was missing required properties: session.")
            : () => new FileOwnership("remote-engine", "/", "", null);
        using var normal = await OpenAsync();
        var answer = await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}","machine":"{{{Remote}}}"}}""");
        Assert.Equal("remote_older", Code(answer));
        Assert.Equal("workbox-test's Assembly is older and can't open files outside a session yet.", Message(answer));
    }

    [Fact]
    public async Task A_null_machine_never_forwards()
    {
        using var normal = await OpenAsync();
        var owner = Result(await CallAsync(normal, 1, "owner", $$$"""{"target":{"path":"{{{Target}}}"}}"""));
        Assert.Equal(_macOwners.ResolvePath(new PathTarget(Target), default).Machine, owner.GetProperty("machine").GetString());
        Assert.NotEqual(await RemoteEngine(), owner.GetProperty("machine").GetString());
        Assert.Equal(0, Calls("owner"));
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
        await SendAsync(socket, """{"t":"hello","protocol":1,"token":"isolated-token"}""");
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
            inner.Read(feature, method, args, result, (value, call) =>
            {
                test._remoteCalls.AddOrUpdate(method, 1, (_, count) => count + 1);
                if (method == "owner" && test._olderOwner is { } older) return Task.FromResult((TResult)(object)older());
                return handler(value, call);
            }, bulk);

        public void Write<TArgs, TResult>(string feature, string method, JsonTypeInfo<TArgs> args, JsonTypeInfo<TResult> result,
            Func<TArgs, CallContext, Task<Func<IWriteScope, TResult>>> handler, Func<TArgs, string?>? order = null,
            Action<TArgs, CallContext>? failed = null, bool bulk = false) =>
            inner.Write(feature, method, args, result, (value, call) =>
            {
                test._remoteCalls.AddOrUpdate(method, 1, (_, count) => count + 1);
                return handler(value, call);
            }, order, failed, bulk);

        public void Save<TValue>(string feature, JsonTypeInfo<TValue> value, Action<string, TValue, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<TItem>(string topic, JsonTypeInfo<TItem> item, Func<TItem, string> key,
            Func<string?, CallContext, IReadOnlyList<TItem>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state,
            Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
