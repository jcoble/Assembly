using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Desktop.Serve;
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

public sealed class CliCheckSocketTests
{
    [Fact]
    public async Task Real_socket_read_returns_only_the_exact_safe_selection_and_component_facts()
    {
        using var f = new CliCheckFixture(); using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var calls = new SocketDispatcher(db.Writer, new TopicSet(), f.Time, new Assembly.Tests.Infrastructure.Socket.SocketTests.FakeLog());
        new UpdateCalls(f.Launcher, "local-machine", f.Http, f.Time, default, (_, _) => null, new RemoteCliClient(f.Http, f.Time)).Register(calls);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets();
        var endpoint = new SocketEndpoint("cli-token", calls, "local-machine", f.Time);
        app.Map("/socket", endpoint.HandleAsync); app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var socket = await Open(address);
        await Send(socket, new { t = "call", id = 1, feature = "updates", method = "checkCli", args = JsonSerializer.SerializeToElement(f.Args, UpdatesJson.Default.CliCheckArgs) });
        var frame = await Read(socket); Assert.Equal("result", frame.GetProperty("t").GetString());
        var result = frame.GetProperty("result");
        Assert.Equal("update_available", result.GetProperty("comparison").GetString());
        Assert.Equal(f.Args.ConfigurationTargetId, result.GetProperty("configurationTargetId").GetString());
        Assert.Equal(f.Args.AccountContext, result.GetProperty("accountContext").GetString());
        Assert.Equal(f.Version, result.GetProperty("installedVersion").GetString());
        Assert.DoesNotContain(f.Home, frame.GetRawText()); Assert.DoesNotContain("PRIVATE_ARG", frame.GetRawText());
        Assert.Equal(0, f.Launcher.Launches);
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_DesktopBackend_registers_readonly_call_and_refuses_other_or_missing_machine_before_execution(bool missing)
    {
        using var f = new CliCheckFixture();
        await using var backend = await DesktopBackend.StartAsync(Path.Combine(f.Home, "desktop"), "cli-token", 0, f.Launcher, TextWriter.Null);
        using var socket = await Open(backend.Address);
        await Send(socket, new
        {
            t = "call",
            id = 1,
            feature = "updates",
            method = "checkCli",
            args = JsonSerializer.SerializeToElement(f.Args with { MachineId = missing ? null! : "different-machine" }, UpdatesJson.Default.CliCheckArgs)
        });
        var frame = await Read(socket); Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Equal("failed", frame.GetProperty("code").GetString());
        Assert.Equal("CLI check unavailable.", frame.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(f.Home, "probe-record"))); Assert.Equal(0, f.Launcher.Launches);
        socket.Abort();
    }

    private static async Task<ClientWebSocket> Open(Uri address)
    {
        var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), timeout.Token);
        await Send(socket, new { t = "hello", protocol = 1, token = "cli-token" });
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task Send(ClientWebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var output = new MemoryStream();
        var buffer = new byte[4096]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token); output.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
}
