using System.Net;
using System.Net.WebSockets;
using System.Text;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Terminal;

public sealed class TerminalSocketTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly FakePtyStarter _pty = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("terminal-socket-");
    private AppDatabase _db = null!;
    private TerminalHost _host = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _host = new TerminalHost(_pty, topics, TimeProvider.System);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new TerminalFeature(_host).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var main = new SocketEndpoint("terminal-token", calls, "test-machine", TimeProvider.System);
        var terminal = new SocketEndpoint("terminal-token", calls, "test-machine", TimeProvider.System, terminal: true);
        _app.Map("/socket", main.HandleAsync);
        _app.Map("/socket/terminal", terminal.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(main.CloseAll);
        _app.Lifetime.ApplicationStopping.Register(terminal.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}");
    }

    [Fact]
    public async Task A_terminal_subscription_is_refused_on_the_main_socket()
    {
        var terminal = await _host.StartAsync(new StartArgs(_folder.FullName));
        using var socket = await OpenAsync("/socket");
        await SendAsync(socket, $$"""{"t":"sub","id":1,"topic":"terminal","key":"{{terminal.Id}}"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"not_found","message":"terminal is on /socket/terminal"}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_call_is_refused_on_the_terminal_socket()
    {
        using var socket = await OpenAsync("/socket/terminal");
        await SendAsync(socket, """{"t":"call","id":1,"feature":"terminal","method":"list","args":{}}""");
        Assert.Equal("""{"t":"error","id":1,"code":"not_found","message":"terminal/list is on /socket"}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_terminal_row_goes_out_as_a_change_on_the_terminal_socket()
    {
        var terminal = await _host.StartAsync(new StartArgs(_folder.FullName));
        using var socket = await OpenAsync("/socket/terminal");
        await SendAsync(socket, $$"""{"t":"sub","id":1,"topic":"terminal","key":"{{terminal.Id}}"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[]}""", await ReadAsync(socket));
        _pty.Started[0].Emit("hello"u8.ToArray());
        Assert.Equal($$$"""{"t":"change","sub":1,"op":"upsert","key":"{{{terminal.Id}}}","item":{"seq":5,"prevSeq":0,"data":"aGVsbG8="}}""", await ReadAsync(socket));
    }

    private async Task<ClientWebSocket> OpenAsync(string path)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_address, path), CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"terminal-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.True(received.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _host.DisposeAsync();
        _db.Dispose();
        _file.Dispose();
        _folder.Delete(recursive: true);
    }
}
