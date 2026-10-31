using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
        public TaskCompletionSource VersionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VersionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _noLive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task NoLiveAsync() { lock (_liveOpen) return _liveOpen.Values.Sum() == 0 ? Task.CompletedTask : _noLive.Task; }
        public int VersionHits;
        public int VersionAborted;
        public int AgentsHits;
        public int LiveHits;
        public int TitleHits;
        public int SendHits;
        public int ConfigSets;
        public Func<CancellationToken, Task>? BeforeConfig { get; set; }
        public int LiveOpen { get { lock (_liveOpen) return _liveOpen.Values.Sum(); } }
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
                VersionStarted.TrySetResult();
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
                        VersionCancelled.TrySetResult();
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
            _app.Map("/socket/{**path}", async (HttpContext context) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var subscriptions = new List<string>();
                async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, context.RequestAborted);
                try
                {
                    var buffer = new byte[16384];
                    while (true)
                    {
                        var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (received.MessageType == WebSocketMessageType.Close) break;
                        using var frame = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                        var json = frame.RootElement;
                        switch (json.GetProperty("t").GetString())
                        {
                            case "hello":
                                await Send("""{"t":"welcome","protocol":1,"machine":"box"}""");
                                break;
                            case "sub":
                                var session = json.GetProperty("key").GetString()!;
                                subscriptions.Add(session);
                                Interlocked.Increment(ref LiveHits);
                                lock (_liveOpen)
                                {
                                    if (_liveOpen.Values.Sum() == 0) _noLive = new(TaskCreationOptions.RunContinuationsAsynchronously);
                                    MostLiveAtOnce = Math.Max(MostLiveAtOnce, _liveOpen[session] = _liveOpen.GetValueOrDefault(session) + 1);
                                    Seen(session).TrySetResult();
                                }
                                await Send($$"""{"t":"state","sub":{{json.GetProperty("id")}},"seq":{{json.GetProperty("after")}},"items":[]}""");
                                break;
                            case "unsub":
                                // This fixture's follower tests only unsubscribe the oldest active session.
                                Close(subscriptions[0]);
                                subscriptions.RemoveAt(0);
                                break;
                            case "call":
                                var after = json.GetProperty("args").GetProperty("after").GetInt64();
                                var rows = LiveRows.Select(row => JsonDocument.Parse(row).RootElement.GetProperty("row").Clone())
                                    .Where(row => row.GetProperty("seq").GetInt64() > after).Select(row => row.GetRawText());
                                await Send("{\"t\":\"result\",\"id\":1,\"result\":{\"rows\":[" + string.Join(',', rows) + "]}}");
                                break;
                        }
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
                finally { foreach (var session in subscriptions) Close(session); }
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
            _app.MapGet("/sessions/{id}/config", async (HttpContext context) =>
            {
                if (BeforeConfig is not null) await BeforeConfig(context.RequestAborted);
                return Results.Text("""{"options":[],"choices":{"where":"remote"}}""", "application/json");
            });
            _app.MapPost("/sessions/{id}/config", () =>
            {
                Interlocked.Increment(ref ConfigSets);
                return Results.Text("""{"options":[],"choices":{"where":"remote"}}""", "application/json");
            });
            _app.MapPost("/sessions/{id}/send", () =>
            {
                Interlocked.Increment(ref SendHits);
                return Results.Text("""{"runId":"remote-run"}""", "application/json");
            });
            _app.MapPost("/runs/{id}/stop", () => Results.Text(StopStatus == 500 ? "boom" : "", statusCode: StopStatus));
            await _app.StartAsync();
            Address = new Uri(_app.Urls.Single());
        }

        private void Close(string session)
        {
            lock (_liveOpen)
            {
                _liveOpen[session]--;
                if (_liveOpen.Values.Sum() == 0) _noLive.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
