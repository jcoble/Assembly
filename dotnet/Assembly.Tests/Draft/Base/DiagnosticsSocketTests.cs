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
using Assembly.Application.Features.Diagnostics;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>The Mac page's diagnostics calls over the socket (B8), one test per call, on a fake host.</summary>
public sealed class DiagnosticsSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly FakeHost _host = new();
    private int _drops;
    private AppDatabase _db = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new DiagnosticsFeature(_host, () => Interlocked.Increment(ref _drops)).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("diagnostics-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task Memory_sample_answers_the_hosts_reading()
    {
        Assert.Equal(
            """{"t":"result","id":1,"result":{"appPid":null,"appRssKb":null,"webContentPid":null,"webContentRssKb":null,"backendPid":42,"backendRssKb":1234}}""",
            await CallAsync("memory.sample", "{}"));
    }

    [Fact]
    public async Task Activity_begin_and_end_hold_and_release_app_nap()
    {
        Assert.Equal("""{"t":"result","id":1,"result":{}}""", await CallAsync("activity.begin", "{}"));
        Assert.Equal(1, _host.Activities);
        Assert.Equal("""{"t":"result","id":1,"result":{}}""", await CallAsync("activity.end", "{}"));
        Assert.Equal(0, _host.Activities);
    }

    [Fact]
    public async Task Drop_drops_every_subscription()
    {
        Assert.Equal("""{"t":"result","id":1,"result":{}}""", await CallAsync("drop", """{"ms":500}"""));
        Assert.Equal(1, _drops);
    }

    [Fact]
    public async Task Soak_append_appends_a_line_to_a_plain_file_name_only()
    {
        Assert.Equal("""{"t":"result","id":1,"result":{}}""", await CallAsync("soak.append", """{"name":"run.csv","line":"1,2"}"""));
        Assert.Contains("\"failed\"", await CallAsync("soak.append", """{"name":"../run.csv","line":"3,4"}"""));
        Assert.Contains("\"failed\"", await CallAsync("soak.append", """{"name":"","line":"5,6"}"""));
        Assert.Equal([("run.csv", "1,2")], _host.Soak);
    }

    private async Task<string> CallAsync(string method, string args)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"diagnostics-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        await SendAsync(socket, $$"""{"t":"call","id":1,"feature":"diagnostics","method":"{{method}}","args":{{args}}}""");
        return await ReadAsync(socket);
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private sealed class FakeHost : IDiagnosticsHost
    {
        public int Activities { get; private set; }
        public List<(string Name, string Line)> Soak { get; } = [];

        public MemoryReading Memory() => new(null, null, null, null, 42, 1234);
        public void BeginActivity() => Activities++;
        public void EndActivity() => Activities--;
        public void AppendSoak(string name, string line) => Soak.Add((name, line));
    }
}
