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

    [Fact]
    public void JsonRoundTripsAllBoardPayloads()
    {
        using var f = new BoardQueryTests.Fixture();
        f.Update(question: f.Ask());
        var page = f.Page();
        var json = JsonSerializer.Serialize(page, WorkflowJson.Default.BoardPageResult);
        var decoded = JsonSerializer.Deserialize(json, WorkflowJson.Default.BoardPageResult)!;
        Assert.Equal(page.Items[0].Task, decoded.Items[0].Task);
        Assert.Contains("\"nextCursor\":null", json);
        Assert.Contains("\"tools\":null", json);
        var feed = f.Queries.Changes(new(0), CancellationToken.None);
        Assert.Equal(feed.Events[0].Task, JsonSerializer.Deserialize(JsonSerializer.Serialize(feed,
            WorkflowJson.Default.BoardChangesResult), WorkflowJson.Default.BoardChangesResult)!.Events[0].Task);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"project":null,"state":null,"cursor":null,"extra":1}""", WorkflowJson.Default.BoardPageArgs));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", WorkflowJson.Default.BoardAnswerArgs));
        var task = page.Items[0].Task;
        var question = f.Detail().Questions[0];
        var answer = f.Answer();
        var acceptance = new BoardAnswerResult("local", "task", "q1", 1, answer.AnswerKey, 2);
        object[] payloads = [
            task, question, feed.Events[0], page.Items[0].Metrics, page.Items[0],
            new BoardCursor(0, 1000, "local", "task"), new BoardQuestionCursor(1000, "q1"),
            page.Counts, page.Watermarks[0], new BoardPageArgs(null, null, null), page,
            new BoardTaskArgs("local", "task", null, null), f.Detail(), new BoardChangesArgs(0), feed,
            new BoardChange("local", "task", 1, 1), answer.Answer, answer, acceptance, f.Ask(),
            new BoardAnswerReceived("q1", 1, answer.AnswerKey, answer.Answer), f.Report(), f.Update()
        ];
        foreach (var payload in payloads)
        {
            var type = WorkflowJson.Default.GetTypeInfo(payload.GetType())!;
            var encoded = JsonSerializer.Serialize(payload, type);
            Assert.Equal(encoded, JsonSerializer.Serialize(JsonSerializer.Deserialize(encoded, type), type));
        }
        // Include the configured empty source watermark without inventing a row or a cursor table.
        var emptyRemote = new global::Assembly.Application.Features.Workflows.WorkflowQueries(f.Db, "Workbox Test")
            .Page(new(null, null, null), CancellationToken.None);
        Assert.Contains(emptyRemote.Watermarks, w => w.MachineId == "Workbox Test" && w.Seq == 0);
    }

    internal sealed class Host : IAsyncDisposable
    {
        public BoardQueryTests.Fixture F { get; } = new();
        public SessionStore Sessions { get; }
        public BoardTools Tools { get; }
        public FakeMachine Machine { get; }
        private WebApplication? _app;
        public Uri Address { get; private set; } = null!;
        public Host()
        {
            Sessions = SessionStore.Open(F.Db, F.Time, "unused");
            Machine = new FakeMachine(null, Sessions);
            Tools = new BoardTools(Sessions, new MachineSet(Machine), F.Transitions, F.Queries, CancellationToken.None);
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
    public async Task EmptyBoardTopicIsNotPage()
    {
        await using var host = new Host();
        await host.StartAsync();
        host.F.Update();
        using var socket = await host.ConnectAsync();
        await Send(socket, """{"t":"sub","id":1,"topic":"board"}""");
        Assert.Empty((await Read(socket)).GetProperty("items").EnumerateArray());
        await Send(socket, """{"t":"call","id":2,"feature":"board","method":"page","args":{"project":null,"state":null,"cursor":null}}""");
        Assert.Equal(1, (await Read(socket)).GetProperty("result").GetProperty("counts").GetProperty("total").GetInt64());
        await Send(socket, """{"t":"call","id":3,"feature":"board","method":"update","args":{}}""");
        Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        await Send(socket, """{"t":"call","id":4,"feature":"board","method":"page","args":{}}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
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
