using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Application.Common;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Terminal;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>The real server's terminal socket, read from a real socket rather than a test host's own endpoint.</summary>
public sealed class TerminalSocketEngineTests
{
    private const string Token = "test-token";

    [Fact]
    public async Task The_terminal_socket_built_by_ApiServer_names_its_engine()
    {
        await using var host = new TwoHosts.Host();
        using var hub = new LiveHub(RowStore.Open(host.Fixture.Db));
        var work = Directory.CreateTempSubdirectory("assembly-terminal-engine-");
        try
        {
            await using var sessions = new SessionRegistry(hub, host.Sessions, new FakeLauncher(), TimeProvider.System, TextWriter.Null,
                new BackgroundWork(TimeProvider.System, TextWriter.Null));
            var attachments = new AttachmentStore(Path.Combine(work.FullName, "attachments"));
            var machines = new MachineSet(new LocalMachine(hub, host.Sessions, sessions, attachments));
            await using var app = ApiServer.Build(new ApiServices(hub, host.Sessions, machines, attachments, Token,
                Writer: host.Fixture.Db.Writer, Topics: host.Topics, Engine: host.Runtime), port: 0, out _);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

            var main = await WelcomeAsync(address, "/socket");
            var terminal = await WelcomeAsync(address, "/socket/terminal");

            Assert.Equal(host.Runtime.Machine, main.GetProperty("engine").GetString());
            Assert.True(terminal.TryGetProperty("engine", out var engine), $"the terminal socket's welcome names no engine: {terminal}");
            Assert.Equal(main.GetProperty("engine").GetString(), engine.GetString());
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_real_ApiServer_terminal_endpoint_connects_through_RemoteTerminalSocket()
    {
        await using var host = new TwoHosts.Host();
        await using var window = new TwoHosts.Host();
        using var hub = new LiveHub(RowStore.Open(host.Fixture.Db));
        using var windowHub = new LiveHub(RowStore.Open(window.Fixture.Db));
        var work = Directory.CreateTempSubdirectory("assembly-terminal-engine-");
        try
        {
            var pty = new FakePtyStarter();
            await using var terminals = new TerminalHost(pty, host.Topics, TimeProvider.System, ("/bin/sh", null), _ => work.FullName);
            await using var sessions = new SessionRegistry(hub, host.Sessions, new FakeLauncher(), TimeProvider.System, TextWriter.Null,
                new BackgroundWork(TimeProvider.System, TextWriter.Null));
            var attachments = new AttachmentStore(Path.Combine(work.FullName, "attachments"));
            var machines = new MachineSet(new LocalMachine(hub, host.Sessions, sessions, attachments));
            await using var app = ApiServer.Build(new ApiServices(hub, host.Sessions, machines, attachments, Token,
                Writer: host.Fixture.Db.Writer, Topics: host.Topics, Engine: host.Runtime,
                Features: [new EngineCalls(host.Runtime, TimeProvider.System, host.Needs), new TerminalFeature(terminals)]), port: 0, out _);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var shell = await terminals.StartAsync(new StartArgs(Cwd: work.FullName, Owner: "session"), default);
            await using var remote = new RemoteMachine("workbox-test", address, Token, windowHub, window.Sessions, TextWriter.Null,
                new MachineStatus(window.Topics, []), topics: window.Topics);
            var json = TerminalJson.Default;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // The main socket verifies the engine first, as a connected machine's has by the time a terminal opens.
            await remote.ProbeAsync(deadline.Token);
            await remote.ReadMirrorOwnerAsync(deadline.Token);

            IReadOnlyList<TerminalInfo>? listed = null;
            var failure = await Record.ExceptionAsync(async () =>
                listed = await remote.Terminals.CallAsync("list", new ListArgs(), json.ListArgs, json.IReadOnlyListTerminalInfo, deadline.Token));

            Assert.Null(failure);
            Assert.Equal(shell.Id, Assert.Single(listed!).Id);
            pty.Started[0].Emit("through the real endpoint"u8.ToArray());
            var piece = await remote.Terminals.CallAsync("read", new ReadArgs(shell.Id, 0), json.ReadArgs, json.OutputPiece, deadline.Token);
            Assert.Equal("through the real endpoint", Encoding.UTF8.GetString(piece.Data));
        }
        finally { work.Delete(recursive: true); }
    }

    private static async Task<JsonElement> WelcomeAsync(Uri address, string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}{path}"), deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"hello","token":"{{Token}}","protocol":1}"""), WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[16 * 1024];
        var received = await socket.ReceiveAsync(buffer, deadline.Token);
        using var welcome = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
        Assert.Equal("welcome", welcome.RootElement.GetProperty("t").GetString());
        return welcome.RootElement.Clone();
    }
}
