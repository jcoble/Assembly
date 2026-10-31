using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Remote;
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
    private readonly TopicSet _remoteTopics = new();
    private SocketEndpoint _terminalEndpoint = null!;
    private AppDatabase _db = null!;
    private TerminalHost _host = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = _remoteTopics;
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _host = new TerminalHost(_pty, topics, TimeProvider.System, _ => _folder.FullName);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        new TerminalFeature(_host).Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var main = new SocketEndpoint("terminal-token", calls, "test-machine", TimeProvider.System);
        var terminal = _terminalEndpoint = new SocketEndpoint("terminal-token", calls, "test-machine", TimeProvider.System, terminal: true);
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
    public async Task Terminal_calls_run_only_on_the_terminal_socket()
    {
        using var socket = await OpenAsync("/socket/terminal");
        await SendAsync(socket, """{"t":"call","id":1,"feature":"terminal","method":"list","args":{}}""");
        Assert.Equal("""{"t":"result","id":1,"result":[]}""", await ReadAsync(socket));
        using var main = await OpenAsync("/socket");
        await SendAsync(main, """{"t":"call","id":2,"feature":"terminal","method":"list","args":{}}""");
        Assert.Contains("terminal/list is on /socket/terminal", await ReadAsync(main));
        await SendAsync(socket, """{"t":"call","id":3,"feature":"chat","method":"list","args":{}}""");
        Assert.Contains("not_found", await ReadAsync(socket));
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

    [Fact]
    public async Task Remote_calls_list_reuse_and_output_are_routed_without_starting_a_local_pty()
    {
        var topics = new TopicSet();
        var sessions = SessionStore.Open(_db, TimeProvider.System, _folder.FullName);
        sessions.Add("remote-session", "claude", "/repo", "workbox-test");
        var localSession = sessions.Create("codex", "/repo").SessionId;
        using var hub = new LiveHub(RowStore.Open(_db));
        var address = new UriBuilder(_address) { Scheme = "http" }.Uri;
        await using var remote = new RemoteMachine("workbox-test", address, "terminal-token", hub, sessions, TextWriter.Null,
            new MachineStatus(topics, []), topics: topics);
        var localPty = new FakePtyStarter();
        await using var local = new TerminalHost(localPty, topics, TimeProvider.System);
        var machines = new MachineSet(remote, remote);
        var routed = new RoutedTerminals(local, sessions, machines);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = limit.Token;
        var started = await routed.StartAsync(new StartArgs(Owner: "remote-session"), ct);
        Assert.StartsWith("assembly-remote-terminal://workbox-test/term-", started.Id);
        Assert.Equal(_folder.FullName, started.Cwd);
        Assert.Empty(localPty.Started);
        Assert.Single(_pty.Started);
        Assert.Equal(started.Id, (await routed.StartAsync(new StartArgs(Owner: "remote-session"), ct)).Id);
        Assert.Equal(started.Id, Assert.Single(await routed.ListAsync("remote-session", ct)).Id);
        Assert.Single(await routed.ListAsync(null, ct));

        var output = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = topics.Subscribe("terminal", started.Id, _ => { }, (_, json) =>
            output.TrySetResult(JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!));
        _pty.Started[0].Emit("remote output"u8.ToArray());
        Assert.Equal("remote output"u8.ToArray(), (await output.Task.WaitAsync(ct)).Data);
        Assert.Equal("remote output"u8.ToArray(), (await routed.ReadAsync(started.Id, 0, ct)).Data);
        await routed.WriteAsync(started.Id, "typed"u8.ToArray(), ct);
        Assert.Equal("typed"u8.ToArray(), Assert.Single(_pty.Started[0].Written));
        await routed.ResizeAsync(started.Id, 120, 30, ct);
        Assert.Equal((120, 30), Assert.Single(_pty.Started[0].Resizes));

        var nativeId = Assert.Single(_host.List()).Id;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribed = disconnected;
        void Unsubscribed(string topic, string? key)
        {
            if (topic == "terminal" && key == nativeId) unsubscribed.TrySetResult();
        }
        _remoteTopics.Unsubscribed += Unsubscribed;
        var caughtUp = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterReconnect = topics.Subscribe("terminal", started.Id, _ => { }, (_, json) =>
        {
            var row = JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!;
            if (row.Exit is not null) caughtUp.TrySetResult(row);
        });
        _terminalEndpoint.DropAll();
        await disconnected.Task.WaitAsync(ct);
        unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pty.Started[0].Emit(" while disconnected"u8.ToArray());
        _pty.Started[0].End(7);
        // No window call drives recovery: the owned remote socket reconnects and signals replay and exit.
        Assert.Equal(7, (await caughtUp.Task.WaitAsync(ct)).Exit);
        await unsubscribed.Task.WaitAsync(ct);
        Assert.Equal(7, Assert.Single(await routed.ListAsync("remote-session", ct)).ExitCode);
        Assert.False(_remoteTopics.HasSubscribers("terminal", nativeId));
        Assert.Equal("remote output while disconnected"u8.ToArray(), (await routed.ReadAsync(started.Id, 0, ct)).Data);
        topics.Unsubscribe(afterReconnect);
        _remoteTopics.Unsubscribed -= Unsubscribed;

        // A missing remote is irrelevant to the selected local session's list.
        machines.Remove("workbox-test");
        Assert.Empty(await routed.ListAsync(localSession, ct));
        machines.Set(remote);
        var reopened = await routed.StartAsync(new StartArgs(Owner: "remote-session"), ct);
        Assert.NotEqual(started.Id, reopened.Id);
        Assert.False(_remoteTopics.HasSubscribers("terminal", nativeId));
        Assert.True(_pty.Started[0].Disposed);
        await routed.CloseAsync(reopened.Id, ct);
        Assert.True(_pty.Started[1].Disposed);
        Assert.Empty(await routed.ListAsync("remote-session", ct));
        topics.Unsubscribe(subscription);
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
