using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Mcp;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Application.Common;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Tests.Features.Chat.Sessions;
using System.Text.Json;
using Assembly.Application.Features.Workflows.Contracts;
using Assembly.Infrastructure.Workflows;

namespace Assembly.Tests.Draft.Board;

public sealed class BoardSocketTests
{
    [Fact]
    public void PublishFollowsCommit()
    {
        using var f = new BoardQueryTests.Fixture();
        long revision = 0;
        var sub = f.Topics.Subscribe("board", null, _ => { }, (_, json) =>
        {
            var change = JsonSerializer.Deserialize(json, WorkflowJson.Default.BoardChange)!;
            revision = f.Detail().Task.Revision;
            Assert.Equal(change.Revision, revision);
        });
        f.Update();
        Assert.Equal(1, revision);
        f.Topics.Unsubscribe(sub);
    }

    internal sealed class Host : IAsyncDisposable
    {
        public BoardQueryTests.Fixture F { get; } = new();
        public SessionStore Sessions { get; }
        public BoardTools Tools { get; }
        public RowStore Rows { get; }
        public FakeLauncher Launcher { get; } = new();
        public SessionRegistry Registry { get; }
        private readonly LiveHub _hub;
        private readonly BackgroundWork _work;
        private WebApplication? _app;
        public Uri Address { get; private set; } = null!;
        public Host()
        {
            Sessions = SessionStore.Open(F.Db, F.Time, F.Folder);
            Rows = RowStore.Open(F.Db);
            _hub = new LiveHub(Rows);
            _work = new BackgroundWork(F.Time, TextWriter.Null);
            Registry = new SessionRegistry(_hub, Sessions, Launcher, F.Time, TextWriter.Null, _work);
            Tools = new BoardTools(Sessions, Registry, F.Transitions, F.Queries, CancellationToken.None);
        }
        public async Task StartAsync(RemoteWorkflowMirror? mirror = null)
        {
            var calls = new SocketDispatcher(F.Db.Writer, F.Topics, F.Time, NullLogger.Instance);
            new WorkflowCalls(F.Queries, Tools.AnswerAsync, mirror).Register(calls);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.UseWebSockets();
            var endpoint = new SocketEndpoint("board-test", calls, "source", F.Time);
            _app.Map("/socket", endpoint.HandleAsync);
            _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
            await _app.StartAsync();
            Address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(Address) { Scheme = "ws", Path = "/socket" }.Uri, CancellationToken.None);
            await Send(socket, """{"t":"hello","protocol":1,"token":"board-test"}""");
            Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
            return socket;
        }
        public Task StopAsync() => _app!.StopAsync();
        public async ValueTask DisposeAsync()
        {
            if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
            await Tools.DisposeAsync();
            await Registry.DisposeAsync();
            await _work.DisposeAsync();
            _hub.Dispose();
            F.Dispose();
        }
    }

    internal static Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    internal static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            bytes.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return JsonDocument.Parse(bytes.ToArray()).RootElement.Clone();
    }

    [Fact]
    public async Task SubscribeBeforeReadCannotLoseInvalidation()
    {
        await using var host = new Host();
        await host.StartAsync();
        using var socket = await host.ConnectAsync();
        await Send(socket, """{"t":"sub","id":1,"topic":"board"}""");
        await Read(socket);
        host.F.Update();
        var change = await Read(socket);
        Assert.Equal("change", change.GetProperty("t").GetString());
        await Send(socket, """{"t":"call","id":2,"feature":"board","method":"page","args":{"project":null,"state":null,"cursor":null}}""");
        var page = (await Read(socket)).GetProperty("result");
        Assert.Equal(change.GetProperty("item").GetProperty("seq").GetInt64(),
            page.GetProperty("watermarks")[0].GetProperty("seq").GetInt64());
    }
}
