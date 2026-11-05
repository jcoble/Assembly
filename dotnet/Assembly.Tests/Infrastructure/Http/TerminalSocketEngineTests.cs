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
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Http;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Remote;
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
