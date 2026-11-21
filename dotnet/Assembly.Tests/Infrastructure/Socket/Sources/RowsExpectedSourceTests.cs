using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class RowsExpectedSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Expected_source_without_a_provider_refuses_watch_page_and_subagents()
    {
        var topics = new TopicSet();
        using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = sessions.Create("codex", "repo").SessionId; f.SeedSession(session);
        using var hub = new LiveHub(RowStore.Open(f.Db), topics: topics);
        var machines = new MachineSet(new FakeMachine(null, sessions));
        await using var host = await SocketHost.Start(f, topics, new RowsFeature(hub, RowStore.Open(f.Db), sessions, machines, new DiskPathProbe(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var expected = new ExpectedSource(f.Machine, 0, null);
        using var control = await host.Open();
        await Subscribe(control, session, expected);
        AssertRefused(await Read(control));
        using var bulk = await host.Open(bulk: true);
        foreach (var method in new[] { "page", "subagents" })
        {
            await Call(bulk, 1, method, session, expected);
            AssertRefused(await Read(bulk));
        }
    }

    [Fact]
    public async Task Local_zero_generation_null_route_works_and_wrong_identity_generation_or_route_refuses()
    {
        var topics = new TopicSet();
        using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = sessions.Create("codex", "repo").SessionId; f.SeedSession(session);
        var store = RowStore.Open(f.Db);
        using var hub = new LiveHub(store, topics: topics);
        Publish(hub, session, "LOCAL_OWNER");
        var machines = new MachineSet(new FakeMachine(null, sessions));
        await using var host = await SocketHost.Start(f, topics,
            new RowsFeature(hub, store, sessions, machines, new DiskPathProbe(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new SessionRowSources(f.Db, sessions, machines)));
        var expected = new ExpectedSource(f.Machine, 0, null);
        using var bulk = await host.Open(bulk: true);
        await Call(bulk, 1, "page", session, expected);
        var page = await Read(bulk); Assert.Equal("result", Type(page)); Assert.Contains("LOCAL_OWNER", page.GetRawText());
        await Call(bulk, 2, "subagents", session, expected);
        Assert.Equal("result", Type(await Read(bulk)));
        using var control = await host.Open();
        await Subscribe(control, session, expected);
        var replay = await ReplayThrough(control, store.LastSeq(session));
        Assert.Contains("LOCAL_OWNER", string.Join("", replay.Select(frame => frame.GetRawText())));
        await Send(control, new { t = "unsub", id = 1 });
        foreach (var wrong in new[] { expected with { Machine = "saved-name" }, expected with { Generation = 1 }, expected with { RouteMachine = "local" } })
        {
            await Subscribe(control, session, wrong);
            AssertRefused(await Read(control));
            await Call(bulk, 3, "page", session, wrong);
            AssertRefused(await Read(bulk));
            await Call(bulk, 4, "subagents", session, wrong);
            AssertRefused(await Read(bulk));
        }
    }

    private static void AssertRefused(JsonElement frame)
    {
        Assert.Equal("error", Type(frame)); Assert.Equal("source_changed", frame.GetProperty("code").GetString());
        Assert.False(frame.TryGetProperty("rows", out _)); Assert.False(frame.TryGetProperty("result", out _));
    }
    private static string? Type(JsonElement frame) => frame.GetProperty("t").GetString();
    private static Task Subscribe(WebSocket socket, string session, ExpectedSource expected) =>
        Send(socket, new { t = "sub", id = 1, topic = "rows", key = session, after = 0, expectedSource = expected });
    private static Task Call(WebSocket socket, int id, string method, string session, ExpectedSource expected, string? after = null) =>
        Send(socket, new { t = "call", id, feature = "rows", method, args = new { session, after, expectedSource = expected } });
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var stop = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream(); var bytes = new byte[65536];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, part.MessageType); buffer.Write(bytes, 0, part.Count);
        } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
    private static async Task<List<JsonElement>> ReplayThrough(WebSocket socket, long through)
    {
        var state = await Read(socket); Assert.Equal("state", Type(state));
        var frames = new List<JsonElement> { state };
        var seq = state.GetProperty("seq").GetInt64();
        while (seq < through)
        {
            var frame = await Read(socket); Assert.Equal("rows", Type(frame));
            var row = frame.GetProperty("row");
            Assert.Equal(seq, row.GetProperty("prevSeq").GetInt64());
            var current = row.GetProperty("seq").GetInt64(); Assert.True(current > seq);
            seq = current; frames.Add(frame);
        }
        Assert.Equal(through, seq);
        return frames;
    }
    private static void Publish(LiveHub hub, string session, string text) =>
        hub.Publish(session, new RowConverter().Apply(new TurnStarted(1000, Guid.NewGuid().ToString("N"), text)));
    private sealed class SocketHost : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private Uri _address = null!;
        private readonly CancellationTokenSource _stop = new();
        internal static async Task<SocketHost> Start(EngineFixture f, TopicSet topics, RowsFeature rows)
        {
            var host = new SocketHost();
            var calls = new SocketDispatcher(f.Db.Writer, topics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            rows.Register(calls);
            calls.Read("probe", "echo", RowJsonContext.Rows.JsonElement, RowJsonContext.Rows.JsonElement, (args, _) => Task.FromResult(args));
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            host._app = builder.Build(); host._app.UseWebSockets();
            void Map(string path, bool bulk) => host._app.Map(path, async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host._stop.Token);
                await new SocketConnection(socket, calls, TimeProvider.System, bulk).RunAsync(Encoding.UTF8.GetBytes("rows-token"), f.Machine, stop.Token, f.Machine);
            });
            Map("/socket", false); Map("/socket/bulk", true);
            host._app.Lifetime.ApplicationStopping.Register(host._stop.Cancel);
            await host._app.StartAsync();
            host._address = new Uri(host._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return host;
        }
        internal async Task<ClientWebSocket> Open(bool bulk = false)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(_address) { Scheme = "ws", Path = bulk ? "/socket/bulk" : "/socket" }.Uri, CancellationToken.None);
            await Send(socket, new { t = "hello", protocol = 2, token = "rows-token" });
            Assert.Equal("welcome", Type(await Read(socket))); return socket;
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); await _app.DisposeAsync(); _stop.Dispose();
        }
    }
}
