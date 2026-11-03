using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliLocatorCallsTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private Assembly.Infrastructure.Persistence.AppDatabase _db = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private RemoteCliLocator _locator = new("actual-machine", Guid.NewGuid().ToString("N"),
        "/owner/private-capture", "/owner/dotnet", ["/owner/Assembly.Remote.dll"], "/owner/runtime",
        [new("configured-id", "codex", "", new string((char)97, 64))]);
    private int _reads;
    private RemoteCliLocatorCalls _feature = null!;

    public async Task InitializeAsync()
    {
        _db = TestDatabase.Open(_file.Path);
        var calls = new SocketDispatcher(_db.Writer, new TopicSet(), TimeProvider.System,
            new Assembly.Tests.Infrastructure.Socket.SocketTests.FakeLog());
        _feature = new(() => { _reads++; return _locator; });
        _feature.Register(calls);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build(); _app.UseWebSockets();
        var endpoint = new SocketEndpoint("locator-token", calls, "actual-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync() { await _app.DisposeAsync(); _db.Dispose(); _file.Dispose(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_socket_returns_the_complete_safe_locator_including_authoritative_empty(bool empty)
    {
        using var fixture = new CliCheckFixture();
        var capture = RemoteCliCaptureTests.Capture(fixture);
        var file = Path.Combine(fixture.Home, "actual-saved-capture");
        await RemoteCliCapture.SaveAsync(file, capture, default);
        _locator = RemoteCliCapture.Locator(capture, file);
        if (empty) _locator = _locator with { Targets = [] };
        using var socket = await Open();
        await Send(socket, """{"t":"call","id":1,"feature":"engine","method":"cli_locators","args":{}}""");
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var result = frame.GetProperty("result").Deserialize(RemoteCliLocatorJson.Default.RemoteCliLocator)!;
        Assert.Equal(_locator.MachineId, result.MachineId);
        Assert.Equal(_locator.InstallationId, result.InstallationId);
        Assert.Equal(_locator.Targets.ToArray(), result.Targets.ToArray());
        Assert.Equal(_locator.CaptureFile, result.CaptureFile);
        var json = frame.GetRawText();
        Assert.DoesNotContain("PRIVATE_ARG", json); Assert.DoesNotContain("PRIVATE_HOME", json);
        Assert.DoesNotContain(capture.AgentsFile, json); Assert.DoesNotContain("descriptor", json);
        Assert.DoesNotContain("environment", json); Assert.DoesNotContain("agentsHash", json);
        Assert.Equal(1, _reads);
        socket.Abort();
    }

    [Theory]
    [InlineData("{\"machineId\":\"other\"}")]
    [InlineData("{\"token\":\"locator-token\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Arguments_cannot_choose_another_host_or_expand_the_zero_argument_contract(string args)
    {
        using var socket = await Open();
        await Send(socket, $$"""{"t":"call","id":1,"feature":"engine","method":"cli_locators","args":{{args}}}""");
        Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
        Assert.Equal(0, _reads); socket.Abort();
    }

    [Fact]
    public async Task Invalid_authentication_closes_before_reading_any_private_locator()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await Send(socket, """{"t":"hello","protocol":1,"token":"wrong"}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(4401, (int)socket.CloseStatus!); Assert.Equal(0, _reads);
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_phone_peer_cannot_read_private_locators(bool phone)
    {
        using var args = JsonDocument.Parse("{}");
        var error = await Assert.ThrowsAsync<CallFailed>(() => _feature.ReadAsync(args.RootElement,
            new(default, "test", Peer: phone ? new PhonePeer("paired-phone") : null)));
        Assert.Equal("forbidden", error.Reason); Assert.Equal(0, _reads);
    }

    [Fact]
    public async Task Invalid_published_projection_is_refused_instead_of_becoming_authoritative()
    {
        _locator = _locator with { Targets = [_locator.Targets[0], _locator.Targets[0]] };
        using var socket = await Open();
        await Send(socket, """{"t":"call","id":1,"feature":"engine","method":"cli_locators","args":{}}""");
        Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
        Assert.Equal(1, _reads); socket.Abort();
    }

    [Fact]
    public async Task Missing_publication_is_unavailable_rather_than_an_authoritative_empty_set()
    {
        using var args = JsonDocument.Parse("{}");
        var missing = new RemoteCliLocatorCalls(() => null!);
        var error = await Assert.ThrowsAsync<CallFailed>(() => missing.ReadAsync(args.RootElement,
            new(default, "test", Peer: new OwnerPeer())));
        Assert.Equal("configuration_unavailable", error.Reason);
        var failed = new RemoteCliLocatorCalls(() => throw RemoteCliCapture.Unavailable());
        error = await Assert.ThrowsAsync<CallFailed>(() => failed.ReadAsync(args.RootElement,
            new(default, "test", Peer: new OwnerPeer())));
        Assert.Equal("configuration_unavailable", error.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_call_or_closed_connection_does_not_read_a_snapshot(bool closed)
    {
        using var args = JsonDocument.Parse("{}");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var call = new CallContext(closed ? default : stop.Token, "test",
            Closed: closed ? stop.Token : default, Peer: new OwnerPeer());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _feature.ReadAsync(args.RootElement, call));
        Assert.Equal(0, _reads);
    }

    private async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(_address, timeout.Token);
        await Send(socket, """{"t":"hello","protocol":1,"token":"locator-token"}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task Send(ClientWebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var output = new MemoryStream();
        var buffer = new byte[4096]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); output.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
}
