using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Assembly.Application.Common;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base.Chat;

/// <summary>
/// A second socket on the same database whose machine is real: each session's host runs its turns on a fake agent,
/// and its calls run on the test's fake clock, so a test can run one past its 30 s limit.
/// </summary>
public sealed partial class ChatSocketTests
{
    private readonly FakeLauncher _launcher = new();
    private readonly SharedLog _log = new();
    // What the real socket's calls log.
    private readonly SocketTests.FakeLog _calls = new();
    private BackgroundWork? _work;
    private SessionRegistry? _registry;
    private WebApplication? _real;
    private Uri _realAddress = null!;

    private async Task<ClientWebSocket> OpenRealAsync()
    {
        if (_real is null)
        {
            _work = new BackgroundWork(_time, _log);
            _registry = new SessionRegistry(_hub, _sessions, _launcher, _time, _log, _work);
            var calls = new SocketDispatcher(_db.Writer, new TopicSet(), _time, _calls);
            new SendFeature(_sessions, new MachineSet(new LocalMachine(_hub, _sessions, _registry, _attachments)), _hub).Register(calls);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _real = builder.Build();
            _real.UseWebSockets();
            var endpoint = new SocketEndpoint("chat-token", calls, "test-machine", TimeProvider.System);
            _real.Map("/socket", endpoint.HandleAsync);
            _real.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
            await _real.StartAsync();
            var http = new Uri(_real.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            _realAddress = new Uri($"ws://{http.Authority}/socket");
        }

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_realAddress, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"chat-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private async Task StopRealAsync()
    {
        if (_real is null)
            return;
        await _real.StopAsync();
        await _real.DisposeAsync();
        await _registry!.DisposeAsync();
        await _work!.DisposeAsync();
    }
}
