using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

public sealed partial class MachinesSocketTests
{
    // The remote's far end: /version and /agents, each able to answer an error status, drop the connection or (/version)
    // hang; /live, which sends its rows and then a ping every 300 ms; and a session read that always fails.
    internal sealed class StubRemote : IAsyncDisposable
    {
        private WebApplication _app = null!;

        public Uri Address { get; private set; } = null!;
        public int Format { get; set; } = RowStore.FormatVersion;
        public int AgentsStatus { get; set; } = 200;
        public int StopStatus { get; set; } = 202;
        public bool Drop { get; set; }
        public bool DropVersion { get; set; }
        public bool HangVersion { get; set; }
        public bool Pings { get; set; } = true;
        public string[] LiveRows { get; set; } = [];
        public int VersionHits;
        public int VersionAborted;
        public int AgentsHits;
        public int LiveHits;
        public int TitleHits;
        // The most /live links any one session had open at once, and each session's first /live link, under _liveOpen.
        public int MostLiveAtOnce;
        private readonly Dictionary<string, int> _liveOpen = [];
        private readonly Dictionary<string, TaskCompletionSource> _liveSeen = [];

        /// <summary>Done once a /live link for <paramref name="session"/> has opened.</summary>
        public Task LiveSeenAsync(string session)
        {
            lock (_liveOpen)
                return Seen(session).Task;
        }

        private TaskCompletionSource Seen(string session) =>
            _liveSeen.TryGetValue(session, out var seen) ? seen : _liveSeen[session] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.UseWebSockets();
            _app.MapGet("/version", async (HttpContext context) =>
            {
                Interlocked.Increment(ref VersionHits);
                if (DropVersion)
                {
                    context.Abort();
                    return Results.Empty;
                }
                if (HangVersion)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref VersionAborted);
                        return Results.Empty;
                    }
                }
                return Results.Text($$"""{"formatVersion":{{Format}},"commit":"abc"}""", "application/json");
            });
            _app.MapGet("/sessions/{id}", () =>
            {
                Interlocked.Increment(ref TitleHits);
                return Results.Text("boom", statusCode: 500);
            });
            _app.Map("/live", async (HttpContext context) =>
            {
                Interlocked.Increment(ref LiveHits);
                var session = context.Request.Query["session"].ToString();
                lock (_liveOpen)
                {
                    MostLiveAtOnce = Math.Max(MostLiveAtOnce, _liveOpen[session] = _liveOpen.GetValueOrDefault(session) + 1);
                    Seen(session).TrySetResult();
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var aborted = context.RequestAborted;
                try
                {
                    foreach (var row in LiveRows)
                        await socket.SendAsync(Encoding.UTF8.GetBytes(row), WebSocketMessageType.Text, endOfMessage: true, aborted);
                    while (Pings)
                    {
                        await socket.SendAsync("""{"type":"ping"}"""u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, aborted);
                        await Task.Delay(300, aborted);
                    }
                    await Task.Delay(Timeout.Infinite, aborted);
                }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException)
                {
                }
                finally
                {
                    lock (_liveOpen)
                        _liveOpen[session]--;
                }
            });
            _app.MapGet("/agents", (HttpContext context) =>
            {
                Interlocked.Increment(ref AgentsHits);
                if (Drop)
                {
                    context.Abort();
                    return Results.Empty;
                }
                return AgentsStatus == 200
                    ? Results.Text("""{"agents":[{"id":"claude","name":"Claude"}]}""", "application/json")
                    : Results.Text("boom", statusCode: AgentsStatus);
            });
            _app.MapPost("/runs/{id}/stop", () => Results.Text(StopStatus == 500 ? "boom" : "", statusCode: StopStatus));
            await _app.StartAsync();
            Address = new Uri(_app.Urls.Single());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
