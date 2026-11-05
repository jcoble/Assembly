using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
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
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>
/// Each turn's token total on its <c>stats</c> row, and <c>rows/tokens</c> adding a session's turns. A real two-turn run
/// on Claude and Codex showed the total restarts each turn, so the session's is the sum.
/// </summary>
public sealed class SessionTokensTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // The turn-end shapes the adapters send: Codex's quota extension at protocol 2, and a usage object (Claude's recording).
    [Theory]
    [InlineData("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","_meta":{"quota":{"token_count":{"totalTokens":14601,"inputTokens":2308,"cachedInputTokens":12288,"cacheWriteInputTokens":0,"outputTokens":5,"reasoningOutputTokens":0}}}}""", 14601)]
    [InlineData("""{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn","usage":{"inputTokens":2,"outputTokens":715,"cachedReadTokens":12932,"cachedWriteTokens":19460,"totalTokens":33109}}""", 33109)]
    public void A_recorded_turn_total_reaches_the_stats_row(string end, long total)
    {
        var reader = new UpdateReader();
        var events = new List<AgentEvent> { new TurnStarted(0, "run-1", "hi") };
        events.AddRange(reader.Read(Update("""{"sessionUpdate":"state_update","state":"running"}"""), 10));
        events.AddRange(reader.Read(Update(end), 20));

        var stats = Converted.From(events).Last(RowKind.Stats);

        Assert.Equal(total, Converted.Get(stats, "totalTokens").GetInt64());
    }

    [Fact]
    public void A_zero_total_is_left_out_of_the_stats_row()
    {
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new TurnEnded(20, "cancelled", new TokenUsage(0, 0, 0, 0, 0)),
            new TurnStarted(30, "run-2", "two"), new TurnEnded(50, "end_turn", new TokenUsage(10, 5, null, null)),
        ]);

        Assert.All(turns.OfKind(RowKind.Stats), row => Assert.False(row.Payload.TryGetProperty("totalTokens", out _)));
    }

    [Fact]
    public void A_two_turn_session_answers_the_sum_of_its_turns()
    {
        using var f = new EngineFixture();
        var store = RowStore.Open(f.Db);
        Commit(store, "s",
        [
            new TurnStarted(0, "run-1", "one"), new TurnEnded(20, "end_turn", new TokenUsage(900, 100, null, null, 33_109)),
            new TurnStarted(30, "run-2", "two"), new TurnEnded(50, "end_turn", new TokenUsage(400, 50, null, null, 14_601)),
        ]);

        Assert.Equal(47_710, store.SessionTokens("s", CancellationToken.None));
    }

    [Fact]
    public void A_session_with_no_stats_answers_null()
    {
        using var f = new EngineFixture();
        var store = RowStore.Open(f.Db);
        Commit(store, "s", [new TurnStarted(0, "run-1", "one")]);
        Commit(store, "other", [new TurnStarted(0, "run-1", "one"), new TurnEnded(20, "end_turn", new TokenUsage(1, 1, null, null, 500))]);

        Assert.Null(store.SessionTokens("s", CancellationToken.None));
    }

    [Fact]
    public void A_cancelled_turn_with_zeros_adds_nothing()
    {
        using var f = new EngineFixture();
        var store = RowStore.Open(f.Db);
        Commit(store, "s",
        [
            new TurnStarted(0, "run-1", "one"), new TurnEnded(20, "end_turn", new TokenUsage(900, 100, null, null, 1_000)),
            new TurnStarted(30, "run-2", "two"), new TurnEnded(50, "cancelled", new TokenUsage(0, 0, 0, 0, 0)),
            new TurnStarted(60, "run-3", "three"), new TurnEnded(80, "end_turn", null),
        ]);

        Assert.Equal(1_000, store.SessionTokens("s", CancellationToken.None));
    }

    [Fact]
    public async Task Rows_tokens_with_an_expected_source_is_answered_not_refused()
    {
        var topics = new TopicSet();
        using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var session = sessions.Create("codex", "repo").SessionId; f.SeedSession(session);
        var store = RowStore.Open(f.Db);
        Commit(store, session,
        [
            new TurnStarted(0, "run-1", "one"), new TurnEnded(20, "end_turn", new TokenUsage(900, 100, null, null, 2_000)),
            new TurnStarted(30, "run-2", "two"), new TurnEnded(50, "end_turn", new TokenUsage(400, 50, null, null, 3_000)),
        ]);
        using var hub = new LiveHub(store, topics: topics);
        var machines = new MachineSet(new FakeMachine(null, sessions));
        var calls = new SocketDispatcher(f.Db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new RowsFeature(hub, store, sessions, machines, new SessionRowSources(f.Db, sessions, machines)).Register(calls);
        await using var app = await Start(calls, f.Machine);
        using var socket = await Connect(app);
        var expected = new ExpectedSource(f.Machine, 0, null);

        await Send(socket, new { t = "call", id = 1, feature = "rows", method = "tokens", args = new { session, expectedSource = expected } });

        Assert.Equal("""{"t":"result","id":1,"result":{"total":5000}}""", await Read(socket));
    }

    private static SessionNotification Update(string json) => new("s", JsonDocument.Parse(json).RootElement.Clone());

    private static void Commit(RowStore store, string session, IEnumerable<AgentEvent> events)
    {
        var converter = new RowConverter();
        foreach (var agentEvent in events)
        {
            var batch = converter.Apply(agentEvent);
            if (batch.Rows.Count > 0) store.Commit(session, batch);
        }
    }

    private static async Task<WebApplication> Start(SocketDispatcher calls, string machine)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        var endpoint = new SocketEndpoint("tokens-token", calls, machine, TimeProvider.System);
        app.Map("/socket", endpoint.HandleAsync);
        app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await app.StartAsync();
        return app;
    }

    private static async Task<ClientWebSocket> Connect(WebApplication app)
    {
        var http = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{http.Authority}/socket"), CancellationToken.None);
        await Send(socket, new { t = "hello", token = "tokens-token", protocol = 1 });
        Assert.StartsWith("""{"t":"welcome""", await Read(socket));
        return socket;
    }

    private static Task Send(WebSocket socket, object value) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> Read(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            frame.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(frame.ToArray());
    }
}
