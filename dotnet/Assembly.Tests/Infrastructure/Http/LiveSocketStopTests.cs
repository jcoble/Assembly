using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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

namespace Assembly.Tests.Infrastructure.Http;

/// <summary>A /live socket that subscribes only after the host began stopping, which the hub's close can't reach.</summary>
public sealed class LiveSocketStopTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly TempDatabase _db = new();
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-live-stop-").FullName;
    private AppDatabase _database = null!;
    private LiveHub _hub = null!;
    private SessionRegistry _sessions = null!;

    public Task InitializeAsync()
    {
        _database = TestDatabase.Open(_db.Path);
        _hub = new LiveHub(RowStore.Open(_database));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_live_socket_that_subscribes_after_stopping_began_ends_at_once()
    {
        var sessionStore = SessionStore.Open(_database, TimeProvider.System);
        _sessions = new SessionRegistry(_hub, sessionStore, new FakeLauncher(), TimeProvider.System, TextWriter.Null,
            new BackgroundWork(TimeProvider.System, TextWriter.Null));
        var attachments = new AttachmentStore(Path.Combine(_work, "attachments"));
        var machines = new MachineSet(new LocalMachine(_hub, sessionStore, _sessions, attachments));
        // Closed by the hub's close at stopping; the held socket goes on to subscribe only after that.
        using var closedFirst = _hub.Subscribe("other", 0);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = ApiServer.Build(new ApiServices(_hub, sessionStore, machines, attachments, Token), port: 0, out _,
            more => more.Use(async (context, next) =>
            {
                if (context.Request.Path == "/live")
                {
                    held.TrySetResult();
                    await Completed(closedFirst.Rows.Completion);
                }

                await next(context);
            }));
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        using var socket = new ClientWebSocket();
        var connecting = socket.ConnectAsync(new Uri($"ws://{address.Authority}/live?session=s&after=0&token={Token}"), CancellationToken.None);
        await held.Task;
        var clock = Stopwatch.StartNew();
        await app.StopAsync();

        // Without the stopping token the handler waits out the host's 8 s shutdown timeout.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {clock.Elapsed.TotalSeconds:0.0} s");
        await Completed(connecting);
    }

    private static async Task Completed(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
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
