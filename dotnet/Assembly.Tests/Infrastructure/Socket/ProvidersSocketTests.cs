using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class ProvidersSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly TempDatabase _file = new();
    private readonly ChallengeBoard _board = new();
    private readonly SocketTests.FakeLog _log = new();
    private Assembly.Infrastructure.Persistence.AppDatabase _db = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private bool _overridePeer;
    private CallPeer? _peer;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.Open(_file.Path);
        var calls = new SocketDispatcher(_db.Writer, new TopicSet(), TimeProvider.System, _log);
        new ProvidersFeature(_board, "local-machine").Register(new PeerGate(calls, this));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("provider-token", calls, "local-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task Challenge_read_is_nullable_and_preserves_the_exact_original_URL_without_granting_consent()
    {
        using var socket = await Open();
        Assert.Equal(JsonValueKind.Null, (await Call(socket, "challenge", new { operationId = "op" })).GetProperty("result").ValueKind);
        const string url = "https://EXAMPLE.com:443/a%2fb?code=URL_CANARY&next=%2F";
        var consent = _board.RequestUrlConsentAsync("op", new Uri(url), CancellationToken.None).AsTask();
        try
        {
            var answer = (await Call(socket, "challenge", new { operationId = "op" })).GetProperty("result");
            Assert.Equal(url, answer.GetProperty("link").GetString());
            Assert.False(consent.IsCompleted);
            Assert.Equal(_board.Current("op")!.Generation, answer.GetProperty("generation").GetInt64());
            Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("URL_CANARY", StringComparison.Ordinal));
        }
        finally { _board.Clear("op"); }
        Assert.False(await consent.WaitAsync(Wait));
    }

    [Fact]
    public async Task URL_acknowledgment_requires_the_displayed_generation_and_exact_original_string_and_consumes_once()
    {
        using var socket = await Open();
        const string url = "https://example.com/login?code=exact%2Fvalue";
        var consent = _board.RequestUrlConsentAsync("op", new Uri(url), CancellationToken.None).AsTask();
        var generation = _board.Current("op")!.Generation;
        try
        {
            AssertError(await Call(socket, "acknowledgeUrl", new { operationId = "op", expectedGeneration = generation, originalUrl = url.Replace("%2F", "%2f") }));
            Assert.False(consent.IsCompleted);
            AssertError(await Call(socket, "acknowledgeUrl", new { operationId = "op", expectedGeneration = generation - 1, originalUrl = url }));
            var result = await Call(socket, "acknowledgeUrl", new { operationId = "op", expectedGeneration = generation, originalUrl = url, machine = "local-machine" });
            Assert.Equal("result", result.GetProperty("t").GetString());
            Assert.True(await consent.WaitAsync(Wait));
            AssertError(await Call(socket, "acknowledgeUrl", new { operationId = "op", expectedGeneration = generation, originalUrl = url }));
            Assert.Null(_board.Current("op"));
        }
        finally { _board.Clear("op"); }
    }

    [Fact]
    public async Task Code_submission_is_generation_fenced_and_delivered_once()
    {
        using var socket = await Open();
        var input = new Input();
        var old = _board.Post("op", null, null, null, true, "terminal", input);
        var current = _board.Post("op", null, null, null, true, "terminal", input);
        AssertError(await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = old.Generation, code = "old" }));
        Assert.Empty(input.Values);
        Assert.Equal("result", (await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = current.Generation, code = "CODE_CANARY" })).GetProperty("t").GetString());
        AssertError(await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = current.Generation, code = "again" }));
        Assert.Equal(["CODE_CANARY"], input.Values);
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("CODE_CANARY", StringComparison.Ordinal));
        _board.Clear("op");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Phone_and_missing_server_peer_are_refused_before_board_lookup_or_input(bool phone)
    {
        _overridePeer = true;
        _peer = phone ? new PhonePeer("paired-phone") : null;
        var input = new Input();
        var challenge = _board.Post("op", null, null, null, true, null, input);
        using var socket = await Open();
        AssertError(await Call(socket, "challenge", new { operationId = "op" }));
        AssertError(await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = challenge.Generation, code = "secret" }));
        AssertError(await Call(socket, "acknowledgeUrl", new { operationId = "op", expectedGeneration = challenge.Generation, originalUrl = "secret" }));
        Assert.Empty(input.Values);
        Assert.Equal(challenge, _board.Current("op"));
        _board.Clear("op");
    }

    [Fact]
    public async Task Foreign_machine_input_cannot_consume_the_local_challenge()
    {
        var input = new Input();
        var challenge = _board.Post("op", null, null, null, true, null, input);
        using var socket = await Open();
        AssertError(await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = challenge.Generation, code = "secret", machine = "other-machine" }));
        Assert.Empty(input.Values);
        Assert.Equal(challenge, _board.Current("op"));
        _board.Clear("op");
    }

    [Fact]
    public async Task Input_exceptions_are_fixed_before_dispatcher_logging_and_never_echo_sensitive_text()
    {
        var challenge = _board.Post("op", null, null, null, true, null, new Input { Fail = true });
        using var socket = await Open();
        var result = await Call(socket, "submitCode", new { operationId = "op", expectedGeneration = challenge.Generation, code = "CODE_CANARY" });
        AssertError(result);
        Assert.DoesNotContain("CODE_CANARY", result.GetRawText());
        Assert.DoesNotContain("EXCEPTION_CANARY", result.GetRawText());
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("CANARY", StringComparison.Ordinal));
        _board.Clear("op");
    }

    private static void AssertError(JsonElement frame)
    {
        Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Equal("failed", frame.GetProperty("code").GetString());
    }

    private async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await Send(socket, new { t = "hello", token = "provider-token", protocol = 1 });
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        return socket;
    }

    private static async Task<JsonElement> Call(ClientWebSocket socket, string method, object args)
    {
        await Send(socket, new { t = "call", id = 1, feature = "providers", method, args });
        return await Read(socket);
    }
    private static Task Send(ClientWebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var stop = new CancellationTokenSource(Wait);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult piece;
        do
        {
            piece = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
            Assert.Equal(WebSocketMessageType.Text, piece.MessageType);
            bytes.Write(buffer, 0, piece.Count);
        } while (!piece.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray());
        return json.RootElement.Clone();
    }

    private sealed class Input : IChallengeInput
    {
        public bool Fail { get; init; }
        public List<string> Values { get; } = [];
        public ValueTask SubmitAsync(string code, CancellationToken ct)
        {
            if (Fail) throw new CallFailed("EXCEPTION_CANARY " + code, "stale_challenge");
            Values.Add(code);
            return ValueTask.CompletedTask;
        }
    }

    // The same server-owned peer injection seam used by MachineDoorTests; no caller argument establishes identity.
    private sealed class PeerGate(ICallRegistry inner, ProvidersSocketTests fixture) : ICallRegistry
    {
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) => inner.Read(feature, method, args, result,
            (a, c) => handler(a, fixture._overridePeer ? c with { Peer = fixture._peer } : c), bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler, Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
