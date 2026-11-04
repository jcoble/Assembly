using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteInstallTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly string _home = Path.Combine(Path.GetTempPath(), "remote-install-test-" + Guid.NewGuid().ToString("N"));
    private Assembly.Infrastructure.Persistence.AppDatabase _db = null!;
    private readonly TopicSet _topics = new();
    private readonly BackgroundWork _work = new(TimeProvider.System, TextWriter.Null);
    private readonly HeldProcess _process = new();
    private RemoteInstallJobs _jobs = null!;
    private SocketDispatcher _calls = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private int _captures;
    private bool _overridePeer;
    private string Staging => Path.Combine(_home, "staging");
    private RemoteInstallBinding Binding => new(new("workbox-test", "workbox-test", 9887, RemoteInstallTarget.Root + "/serve/env"),
        RemoteInstallTarget.Engine, _ => { }, _ => { }, _ => { });

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        _db = TestDatabase.Open(_file.Path);
        _jobs = new(_work, _topics, TimeProvider.System, Staging, (_, _, _) => { _captures++; return Task.FromResult(Binding); }, _process);
        _calls = new(_db.Writer, _topics, TimeProvider.System, new Assembly.Tests.Infrastructure.Socket.SocketTests.FakeLog());
        new RemoteInstallFeature(_jobs).Register(new PeerGate(_calls, this));
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build(); _app.UseWebSockets();
        var endpoint = new SocketEndpoint("install-test-token", _calls, "test-desktop", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync); _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new("ws://" + address.Authority + "/socket");
    }
    public async Task DisposeAsync()
    { await _jobs.DisposeAsync(); await _app.DisposeAsync(); await _work.DisposeAsync(); _db.Dispose(); _file.Dispose(); Directory.Delete(_home, true); }

    [Theory]
    [InlineData("inspect")]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("cancel")]
    public async Task Registered_reads_and_writes_require_owner_before_any_file_or_target_work(string method)
    {
        using var socket = await Open();
        _overridePeer = true;
        var request = new RemoteInstallStart(Guid.NewGuid(), "workbox-test", RemoteInstallTarget.Engine, "/does-not-exist", new('a', 64));
        var args = method switch
        {
            "inspect" => JsonSerializer.SerializeToElement(new RemoteInstallInspect(request.BundleFile), RemoteInstallJson.Default.RemoteInstallInspect),
            "start" => JsonSerializer.SerializeToElement(request, RemoteInstallJson.Default.RemoteInstallStart),
            _ => JsonSerializer.SerializeToElement(new RemoteInstallOperation(request.OperationId), RemoteInstallJson.Default.RemoteInstallOperation)
        };
        await Send(socket, new { t = "call", id = 1, feature = "remoteInstall", method, args });
        var reply = await Answer(socket, 1);
        Assert.Equal("forbidden", reply.GetProperty("code").GetString());
        Assert.Equal(0, _captures); Assert.Equal(0, _process.Stages);
        Assert.False(Directory.Exists(Staging));
        socket.Abort();
    }

    [Fact]
    public async Task Rolled_back_admission_starts_no_job_or_staging_work()
    {
        var request = new RemoteInstallStart(Guid.NewGuid(), "workbox-test", RemoteInstallTarget.Engine, Path.Combine(_home, "missing.tar.gz"), new('a', 64));
        var binding = await _jobs.Capture(request, default);
        Assert.Throws<InvalidOperationException>(() => _db.Writer.Write<int>("test.install.rollback", scope =>
        { _jobs.Admit(request, binding, scope); throw new InvalidOperationException("rollback"); }));
        Assert.Null(_jobs.TryStatus(request.OperationId)); Assert.Equal(0, _work.Running); Assert.Equal(0, _process.Stages);
    }

    [Fact]
    public async Task Registered_subscribe_before_start_duplicate_and_cancel_use_owned_copy_and_push_terminal_cleanup()
    {
        var bundle = await Bundle();
        var selected = await RemoteInstallPackage.InspectAsync(bundle, default);
        var request = new RemoteInstallStart(Guid.NewGuid(), "workbox-test", RemoteInstallTarget.Engine, bundle, selected.Bundle.Sha256);
        var terminal = new TaskCompletionSource<RemoteInstallJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observed = _topics.Subscribe("remoteInstall", request.OperationId.ToString("D"), _ => { }, (_, json) =>
        {
            var job = JsonSerializer.Deserialize(json, RemoteInstallJson.Default.RemoteInstallJob)!;
            if (job.State is "cancelled" or "failed" or "uncertain" or "succeeded") terminal.TrySetResult(job);
        });
        using var socket = await Open();
        await Send(socket, new { t = "sub", id = 1, topic = "remoteInstall", key = request.OperationId.ToString("D") });
        var initial = await Read(socket); Assert.Equal("state", initial.GetProperty("t").GetString());
        Assert.Empty(initial.GetProperty("items").EnumerateArray());
        await Send(socket, new { t = "call", id = 2, feature = "remoteInstall", method = "start", args = request });
        var accepted = await Answer(socket, 2); Assert.Equal("result", accepted.GetProperty("t").GetString());
        await _process.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(bundle, _process.Snapshot); Assert.True(File.Exists(_process.Snapshot));
        await File.WriteAllTextAsync(bundle, "changed after owned snapshot");
        await Send(socket, new { t = "call", id = 3, feature = "remoteInstall", method = "start", args = request });
        Assert.Equal("result", (await Answer(socket, 3)).GetProperty("t").GetString()); Assert.Equal(1, _process.Stages);
        await Send(socket, new { t = "call", id = 4, feature = "remoteInstall", method = "start", args = request with { Sha256 = new('c', 64) } });
        var conflict = await Answer(socket, 4);
        Assert.Equal("operation_id_conflict", conflict.GetProperty("code").GetString()); Assert.Equal(1, _process.Stages);
        await Send(socket, new { t = "call", id = 5, feature = "remoteInstall", method = "cancel", args = new { request.OperationId } });
        Assert.Equal("result", (await Answer(socket, 5)).GetProperty("t").GetString());
        var stopped = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", stopped.State); Assert.Equal(1, _process.Cleanups); Assert.False(File.Exists(_process.Snapshot));
        Assert.Equal(CallKind.Write, _calls.KindOf("remoteInstall", "start")); Assert.Equal(CallKind.Write, _calls.KindOf("remoteInstall", "cancel"));
        socket.Abort();
    }

    [Fact]
    public void Unknown_operation_is_explicit_after_host_restart()
    {
        var error = Assert.Throws<CallFailed>(() => _jobs.Status(Guid.NewGuid()));
        Assert.Equal("operation_unknown", error.Reason);
    }

    private async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket(); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(_address, limit.Token);
        await Send(socket, new { t = "hello", token = "install-test-token", protocol = 1 });
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task Send(ClientWebSocket socket, object frame) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> Answer(ClientWebSocket socket, int id)
    {
        while (true) { var frame = await Read(socket); if (frame.TryGetProperty("id", out var found) && found.GetInt32() == id && frame.GetProperty("t").GetString() is "result" or "error") return frame; }
    }
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var output = new MemoryStream();
        var buffer = new byte[8192]; ValueWebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(buffer.AsMemory(), limit.Token); output.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
    // Keep the real authenticated socket/write pump; only the registered handler's peer is overridden.
    private sealed class PeerGate(ICallRegistry inner, RemoteInstallTests fixture) : ICallRegistry
    {
        private CallContext Peer(CallContext call) => fixture._overridePeer ? call with { Peer = null } : call;
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) => inner.Read(feature, method, args, result, (a, c) => handler(a, Peer(c)), bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler, Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, (a, c) => handler(a, Peer(c)), order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
    private sealed class HeldProcess() : RemoteInstallProcess(TimeProvider.System)
    {
        internal int Stages, Cleanups;
        internal string Snapshot = "";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override async Task<RemoteInstallStaged> StageAsync(RemoteInstallStart request, RemoteInstallBinding binding,
            RemoteInstallPackage.Checked package, string snapshotFile, Action<long> progress, CancellationToken ct)
        {
            Stages++; Snapshot = snapshotFile; progress(package.Bundle.Bytes); Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException("unreachable");
        }
        internal override Task CleanupAsync(RemoteInstallStart request, RemoteInstallBinding binding, CancellationToken ct)
        { Cleanups++; return Task.CompletedTask; }
    }
}
