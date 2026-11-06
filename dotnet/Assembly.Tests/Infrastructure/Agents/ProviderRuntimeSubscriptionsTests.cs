using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class ProviderRuntimeSubscriptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_selected_keys_follow_revisions_and_last_unsubscribe_or_socket_close_joins_the_owner(bool closeSocket)
    {
        await using var fixture = new ProviderRuntimeRemoteFixture();
        await fixture.Start();
        Assert.False(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic));
        using var client = await fixture.Open();
        await ProviderRuntimeRemoteFixture.Subscribe(client, 1, fixture.RemoteMachine);
        await ProviderRuntimeRemoteFixture.Revision(client, fixture.RemoteMachine, fixture.Remote.Revision);
        Assert.True(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic, fixture.RemoteMachine));

        var saved = fixture.Save("/fixture/selected-change");
        var changed = await ProviderRuntimeRemoteFixture.Revision(client, fixture.RemoteMachine, saved.Revision);
        Assert.Equal(["machine", "revision"], changed.GetProperty("item").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("/fixture/desktop", fixture.Local.Read("codex").Settings.ConfigHome);
        var left = fixture.OwnerUnsubscribed();
        if (closeSocket) await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
        else await ProviderRuntimeRemoteFixture.Send(client, """{"t":"unsub","id":1}""");
        await left.WaitAsync(ProviderRuntimeRemoteFixture.Wait);
        Assert.False(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic, fixture.RemoteMachine));
        await fixture.Subscriptions.DisposeAsync();
    }

    [Fact]
    public async Task A_new_socket_opened_while_last_key_close_is_in_flight_keeps_its_live_revision_watch()
    {
        await using var fixture = new ProviderRuntimeRemoteFixture();
        await fixture.Start();
        using var first = await fixture.Open();
        using var next = await fixture.Open();
        await ProviderRuntimeRemoteFixture.Subscribe(first, 1, fixture.RemoteMachine);
        await ProviderRuntimeRemoteFixture.Revision(first, fixture.RemoteMachine, fixture.Remote.Revision);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void LastKey(string topic, string? key)
        {
            if (topic != AgentsFile.Topic || key != fixture.RemoteMachine) return;
            closing.TrySetResult();
            release.Task.GetAwaiter().GetResult(); // Gate the actual out-of-lock close notification, not a timer.
        }
        fixture.Hosts.Mac.Topics.Unsubscribed += LastKey;
        try
        {
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
            await closing.Task.WaitAsync(ProviderRuntimeRemoteFixture.Wait);
            await ProviderRuntimeRemoteFixture.Subscribe(next, 2, fixture.RemoteMachine);
            await ProviderRuntimeRemoteFixture.Revision(next, fixture.RemoteMachine, fixture.Remote.Revision);
            var saved = fixture.Save("/fixture/new-socket-keeps-demand");
            await ProviderRuntimeRemoteFixture.Revision(next, fixture.RemoteMachine, saved.Revision);
            Assert.NotNull(fixture.Subscriptions.Capture(fixture.RemoteMachine));
        }
        finally
        {
            release.TrySetResult();
            fixture.Hosts.Mac.Topics.Unsubscribed -= LastKey;
        }
    }

    [Fact]
    public async Task Actual_owner_socket_reconnect_restores_selected_revision_with_same_engine_and_new_generation()
    {
        await using var fixture = new ProviderRuntimeRemoteFixture();
        await fixture.Start();
        using var client = await fixture.Open();
        await ProviderRuntimeRemoteFixture.Subscribe(client, 1, fixture.RemoteMachine);
        await ProviderRuntimeRemoteFixture.Revision(client, fixture.RemoteMachine, fixture.Remote.Revision);
        var before = Assert.IsType<ProviderRuntimeSubscriptions.Projection>(fixture.Subscriptions.Capture(fixture.RemoteMachine));
        fixture.Hosts.Remote.Endpoint.DropAll();
        await ProviderRuntimeRemoteFixture.Invalidated(before.Changed);
        var saved = fixture.Save("/fixture/reconnected");
        await ProviderRuntimeRemoteFixture.Revision(client, fixture.RemoteMachine, saved.Revision);
        var after = Assert.IsType<ProviderRuntimeSubscriptions.Projection>(fixture.Subscriptions.Capture(fixture.RemoteMachine));
        Assert.Equal(before.Value.Machine, after.Value.Machine);
        Assert.NotSame(before.Epoch.Live, after.Epoch.Live);
        Assert.Throws<Assembly.Application.Common.Calls.CallFailed>(before.EnsureCurrent);
        Assert.True(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic, fixture.RemoteMachine));

        var left = fixture.OwnerUnsubscribed();
        await fixture.Subscriptions.DisposeAsync();
        await left.WaitAsync(ProviderRuntimeRemoteFixture.Wait);
        Assert.Null(fixture.Subscriptions.Capture(fixture.RemoteMachine));
    }

    [Fact]
    public async Task Replaced_profile_owner_does_not_forward_the_new_engine_to_the_old_selected_key()
    {
        await using var fixture = new ProviderRuntimeRemoteFixture();
        await fixture.Start();
        using var client = await fixture.Open();
        var previousMachine = fixture.RemoteMachine;
        await ProviderRuntimeRemoteFixture.Subscribe(client, 1, previousMachine);
        await ProviderRuntimeRemoteFixture.Revision(client, previousMachine, fixture.Remote.Revision);
        var previous = fixture.Subscriptions.Capture(previousMachine)!;
        await fixture.ReplaceOwner();
        await ProviderRuntimeRemoteFixture.Invalidated(previous.Changed);
        Assert.Null(fixture.Subscriptions.Capture(previousMachine));
        await ProviderRuntimeRemoteFixture.Subscribe(client, 2, fixture.RemoteMachine);
        var changed = await ProviderRuntimeRemoteFixture.Revision(client, fixture.RemoteMachine, fixture.Remote.Revision);
        Assert.Equal(2, changed.GetProperty("sub").GetInt64());
        Assert.NotEqual(previousMachine, fixture.RemoteMachine);
        Assert.False(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic, previousMachine));
        Assert.True(fixture.Hosts.Remote.Topics.HasSubscribers(AgentsFile.Topic, fixture.RemoteMachine));
    }
}

// All roles own real SQLite engines, descriptor files and authenticated loopback owner sockets. No adapter executes.
internal sealed class ProviderRuntimeRemoteFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-runtime-revisions-").FullName;
    internal TwoHosts Hosts { get; } = new(forwardCommands: true);
    internal AgentsFile Local { get; }
    internal AgentsFile Remote { get; private set; }
    internal string RemoteMachine => Hosts.Remote.Runtime.Machine;
    internal ProviderRuntimeSubscriptions Subscriptions { get; }
    private int _disposed;

    internal ProviderRuntimeRemoteFixture()
    {
        Local = Source("desktop.json", "/fixture/desktop");
        Remote = Source("remote.json", "/fixture/remote");
        Local.BindOwner(Hosts.Mac.Runtime.Machine, Hosts.Mac.Topics);
        Remote.BindOwner(RemoteMachine, Hosts.Remote.Topics);
        Subscriptions = new(Hosts.Mac.Topics, () => Hosts.Connections.CaptureMirrorOwners());
        Hosts.Mac.AdditionalFeatures = calls => new ProviderRuntimeCalls(Hosts.Mac.Runtime.Machine, Local,
            Hosts.Mac.Forwarder, Subscriptions).Register(calls);
        Hosts.Remote.AdditionalFeatures = calls => new ProviderRuntimeCalls(RemoteMachine, Remote).Register(calls);
    }

    internal async Task Start() { await Hosts.Start(); await Hosts.StartMirror(); }

    internal ProviderRuntimeReply Save(string home)
    {
        var current = Remote.Read("codex");
        return Remote.Save("codex", current.Revision, current.Settings with { ConfigHome = home });
    }

    internal Task OwnerUnsubscribed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var topics = Hosts.Remote.Topics;
        var machine = RemoteMachine;
        void Left(string topic, string? key)
        {
            if (topic != AgentsFile.Topic || key != machine) return;
            topics.Unsubscribed -= Left;
            done.TrySetResult();
        }
        topics.Unsubscribed += Left;
        return done.Task;
    }

    internal async Task ReplaceOwner()
    {
        var replacement = new TwoHosts.Host(true);
        var source = Source("replacement.json", "/fixture/replacement");
        source.BindOwner(replacement.Runtime.Machine, replacement.Topics);
        replacement.AdditionalFeatures = calls => new ProviderRuntimeCalls(replacement.Runtime.Machine, source).Register(calls);
        await Hosts.ReplaceAtSameEndpoint(replacement);
        Remote = source;
    }

    private AgentsFile Source(string name, string home)
    {
        var path = Path.Combine(_home, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new
        {
            id = "codex", name = "Codex", vendorFamily = "codex", command = "never-execute", args = Array.Empty<string>(),
            env = new Dictionary<string, string> { ["CODEX_HOME"] = home }
        } }));
        return new(path);
    }

    internal async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{Hosts.Mac.Address.Authority}/socket"), default);
        await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        return socket;
    }

    internal static Task Subscribe(WebSocket socket, int id, string machine) =>
        Send(socket, JsonSerializer.Serialize(new { t = "sub", id, topic = AgentsFile.Topic, key = machine }));

    internal static async Task<JsonElement> Revision(WebSocket socket, string machine, string revision)
    {
        using var stop = new CancellationTokenSource(Wait);
        while (true)
        {
            var frame = await Read(socket, stop.Token);
            Assert.NotEqual("error", frame.GetProperty("t").GetString());
            if (frame.GetProperty("t").GetString() != "change") continue;
            var item = frame.GetProperty("item");
            Assert.Equal(machine, item.GetProperty("machine").GetString());
            if (item.GetProperty("revision").GetString() == revision) return frame;
        }
    }

    internal static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
    internal static async Task<JsonElement> Read(WebSocket socket, CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(Wait);
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult piece;
        do
        {
            piece = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, piece.MessageType);
            data.Write(buffer, 0, piece.Count);
        } while (!piece.EndOfMessage);
        using var json = JsonDocument.Parse(data.ToArray());
        return json.RootElement.Clone();
    }
    internal static async Task Invalidated(CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => done.TrySetResult());
        await done.Task.WaitAsync(Wait, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Subscriptions.DisposeAsync();
        await Hosts.DisposeAsync();
        Directory.Delete(_home, true);
    }
}
