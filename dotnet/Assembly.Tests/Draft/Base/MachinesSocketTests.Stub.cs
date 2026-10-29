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
        public int DropWelcomes;
        public Func<string, string, string?>? Reply { get; set; }
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

        public async Task StartAsync(Action<WebApplication>? map = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.UseWebSockets();
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
                                if (!context.Request.Path.Value!.EndsWith("bulk", StringComparison.Ordinal))
                                {
                                    Interlocked.Increment(ref VersionHits);
                                    VersionStarted.TrySetResult();
                                }
                                if (DropVersion || Interlocked.Decrement(ref DropWelcomes) >= 0) { context.Abort(); return; }
                                if (HangVersion)
                                {
                                    // The client cancels the hello by closing its owned socket.
                                    try { await socket.ReceiveAsync(buffer, context.RequestAborted); }
                                    finally { Interlocked.Increment(ref VersionAborted); VersionCancelled.TrySetResult(); }
                                    return;
                                }
                                await Send($$"""{"t":"welcome","protocol":1,"machine":"box","format":{{Format}}}""");
                                break;
                            case "sub":
                                if (json.GetProperty("topic").GetString() == "sessions")
                                {
                                    await Send($$"""{"t":"state","sub":{{json.GetProperty("id")}},"items":[]}""");
                                    break;
                                }
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
                                if (subscriptions.Count > 0) { Close(subscriptions[0]); subscriptions.RemoveAt(0); }
                                break;
                            case "call":
                                var id = json.GetProperty("id");
                                var method = json.GetProperty("method").GetString();
                                var feature = json.GetProperty("feature").GetString();
                                string result;
                                string? custom;
                                try { custom = Reply?.Invoke(feature!, method!); }
                                catch (CallFailed error)
                                {
                                    await Send(JsonSerializer.Serialize(new { t = "error", id = id.GetInt64(), code = "failed", message = error.Message }));
                                    break;
                                }
                                if (custom is not null) result = custom;
                                else if (feature == "machines")
                                {
                                    Interlocked.Increment(ref AgentsHits);
                                    if (Drop) { context.Abort(); return; }
                                    if (AgentsStatus != 200)
                                    {
                                        var code = AgentsStatus is 401 or 403 ? "unreachable" : "failed";
                                        var message = code == "unreachable" ? "the remote refused this computer's token" : "boom";
                                        await Send($$"""{"t":"error","id":{{id}},"code":"{{code}}","message":"{{message}}"}""");
                                        break;
                                    }
                                    result = """{"agents":[{"id":"claude","name":"Claude"},{"id":"codex","name":"Codex"}]}""";
                                }
                                else if (feature == "config")
                                {
                                    if (method == "set") Interlocked.Increment(ref ConfigSets);
                                    else if (BeforeConfig is not null) await BeforeConfig(context.RequestAborted);
                                    result = """{"options":[],"choices":{"where":"remote"}}""";
                                }
                                else if (feature == "send")
                                {
                                    if (method == "send")
                                    {
                                        Interlocked.Increment(ref SendHits);
                                        result = """{"runId":"remote-run"}""";
                                    }
                                    else if (StopStatus == 202) result = """{"ok":true}""";
                                    else
                                    {
                                        var message = StopStatus == 404 ? "that run isn't running" : "boom";
                                        await Send($$"""{"t":"error","id":{{id}},"code":"failed","message":"{{message}}"}""");
                                        break;
                                    }
                                }
                                else
                                {
                                    var after = json.GetProperty("args").GetProperty("after").GetInt64();
                                    var rows = LiveRows.Select(row => JsonDocument.Parse(row).RootElement.GetProperty("row").Clone())
                                        .Where(row => row.GetProperty("seq").GetInt64() > after).Select(row => row.GetRawText());
                                    result = "{\"rows\":[" + string.Join(',', rows) + "]}";
                                }
                                await Send($$"""{"t":"result","id":{{id}},"result":{{result}}}""");
                                break;
                        }
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
                finally { foreach (var session in subscriptions) Close(session); }
            });
            map?.Invoke(_app);
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
