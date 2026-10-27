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
using Assembly.Application.Features.Preferences;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// The preferences over the socket (B7): the <c>preferences</c> topic and <c>preferences/set</c>, on a backend with a
/// real database file, which a test can stop and start again on the same file.
/// </summary>
public sealed class PreferencesSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private AppDatabase _db = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        await StopAsync();
        _file.Dispose();
    }

    // Pins: base#7
    [Fact]
    public async Task A_theme_change_is_written_when_made_and_is_there_after_a_restart()
    {
        using (var socket = await OpenAsync())
        {
            await SendAsync(socket, """{"t":"call","id":1,"feature":"preferences","method":"set","args":{"key":"theme","value":"dark"}}""");
            Assert.Equal("""{"t":"result","id":1,"result":null}""", await ReadAsync(socket));
        }

        await StopAsync();
        await StartAsync();

        using var reopened = await OpenAsync();
        await SendAsync(reopened, """{"t":"sub","id":1,"topic":"preferences"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"theme","item":{"key":"theme","value":"dark"}}]}""", await ReadAsync(reopened));
    }

    [Fact]
    public async Task A_change_saved_on_one_socket_reaches_another_sockets_subscription()
    {
        using var window = await OpenAsync();
        using var other = await OpenAsync();
        await SendAsync(other, """{"t":"sub","id":7,"topic":"preferences"}""");
        Assert.Equal("""{"t":"state","sub":7,"items":[]}""", await ReadAsync(other));

        await SendAsync(window, """{"t":"call","id":1,"feature":"preferences","method":"set","args":{"key":"sessionsWidth","value":280}}""");
        Assert.Equal("""{"t":"result","id":1,"result":null}""", await ReadAsync(window));

        Assert.Equal("""{"t":"change","sub":7,"op":"upsert","key":"sessionsWidth","item":{"key":"sessionsWidth","value":280}}""",
            await ReadAsync(other));
    }

    [Fact]
    public async Task A_subscription_that_starts_after_a_save_gets_the_saved_value()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"call","id":1,"feature":"preferences","method":"set","args":{"key":"theme","value":"light"}}""");
        Assert.Equal("""{"t":"result","id":1,"result":null}""", await ReadAsync(socket));

        using var later = await OpenAsync();
        await SendAsync(later, """{"t":"sub","id":1,"topic":"preferences"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"theme","item":{"key":"theme","value":"light"}}]}""", await ReadAsync(later));
    }

    private async Task StartAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new PreferencesFeature(_db).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("prefs-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    private async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _db.Dispose();
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"prefs-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
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
}
