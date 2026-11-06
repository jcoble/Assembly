using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Assembly.Infrastructure.Http;
using Assembly.Application.Common;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Application.Features.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>Retained owner sockets accepted during shutdown must not hold the host open.</summary>
public sealed class LiveSocketStopTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-socket-stop-").FullName;
    private readonly TopicSet _topics = new();
    private AppDatabase _database = null!;
    private LiveHub _hub = null!;
    private SessionRegistry _sessions = null!;

    public Task InitializeAsync()
    {
        _database = AppDatabase.Open(_db.Path, TimeProvider.System, _topics);
        _hub = new LiveHub(RowStore.Open(_database));
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("/socket")]
    [InlineData("/socket/bulk")]
    [InlineData("/socket/terminal")]
    public async Task A_retained_socket_accepted_after_stopping_began_ends_at_once(string path)
    {
        var sessionStore = SessionStore.Open(_database, TimeProvider.System, "checkouts");
        _sessions = new SessionRegistry(_hub, sessionStore, new FakeLauncher(), TimeProvider.System, TextWriter.Null,
            new BackgroundWork(TimeProvider.System, TextWriter.Null));
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, sessionStore, _sessions, attachments));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = ApiServer.Build(new ApiServices(_hub, sessionStore, machines, attachments, Token,
            Writer: _database.Writer, Topics: _topics), port: 0, out _,
            more => more.Use(async (context, next) =>
            {
                if (context.Request.Path == path)
                {
                    held.TrySetResult();
                    await stopping.Task;
                }
                await next(context);
            }));
        using var release = app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ClientWebSocket();
        var connecting = socket.ConnectAsync(new Uri($"ws://{address.Authority}{path}"), deadline.Token);
        await held.Task.WaitAsync(deadline.Token);
        var clock = Stopwatch.StartNew();

        await app.StopAsync(deadline.Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {clock.Elapsed.TotalSeconds:0.0} s");
        try
        {
            await connecting;
            var buffer = new byte[1024];
            var answer = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Close, answer.MessageType);
        }
        catch (WebSocketException)
        {
            Assert.NotEqual(WebSocketState.Open, socket.State);
        }
    }

    public async Task DisposeAsync()
    {
        if (_sessions is not null)
            await _sessions.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
        Directory.Delete(_work, recursive: true);
    }
}
