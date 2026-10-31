using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Desktop.Serve;
using Assembly.Infrastructure.Engine;
using Assembly.Remote.Serve;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Http;

public sealed class EngineHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Real_host_drives_core_and_reconnects(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-host-").FullName;
        var fake = new HostEffects();
        try
        {
            string machineBefore, answerBefore;
            long cursor;
            var answer = new WindowCommand(Guid.NewGuid(), "need", 1, 0,
                new ResolveNeed("answered", JsonSerializer.SerializeToElement("allow")));
            await using (var running = await Host.Start(host, home, fake))
            {
                using var socket = await running.Open();
                using var bulk = await running.Open(bulk: true);
                await Command(socket, "owner", new StartSession("claude", "model", home, "Owner", null, null, null));
                await running.WaitState("sessions", "owner", "idle");
                await Command(socket, "run", new StartRun("owner", "https://example.com/task", null, null, 60000,
                    [new("step", "Step", true, 1, false, [])]));
                await Command(socket, "child", new StartSession("codex", "model", home, "Child", "owner", "run", null));
                await running.WaitState("sessions", "child", "idle");
                fake.Submit("step", new StartStep("child"), 1, new(ActorKind.Session, "owner"));
                fake.Submit("need", new OpenNeed("child", "run", "step", "permission", "Allow?", ["allow", "deny"], "request-1", null, null));
                await Call(socket, "command", JsonSerializer.SerializeToElement(answer, EngineSocketJson.Default.WindowCommand));
                await running.WaitState("needs_me", "need", "answered");
                fake.Submit("message", new SendMessage("owner", "child", "Cross-provider message", "prompt"),
                    actor: new(ActorKind.Session, "owner"));
                await running.WaitState("messages", "message", "delivered_to_turn");
                var child = await Entity(bulk, "session", "child");
                fake.Submit("child", new SetSessionState("ended", null, null), child.GetProperty("version").GetInt64());
                await running.WaitState("messages", "child-ended/child", "delivered_to_turn");
                var need = await Entity(bulk, "needs_me", "need");
                Assert.Equal("answered", need.GetProperty("state").GetString());
                answerBefore = need.GetRawText();
                machineBefore = (await Entity(bulk, "session", "owner")).GetProperty("machine").GetString()!;
                Assert.Equal(fake.Runtime.Machine, machineBefore);
                var message = await Entity(bulk, "message", "message");
                Assert.Equal("owner", message.GetProperty("fromSessionId").GetString());
                Assert.Equal("child", message.GetProperty("toSessionId").GetString());
                Assert.Equal("delivered_to_turn", message.GetProperty("state").GetString());
                Assert.Single(fake.AnsweredRequestIds);
                Assert.Contains("child-ended/child", await running.SnapshotIds("messages"));
                var usage = await Call(socket, "usage", new { entityKind = "session", entityId = "owner" });
                Assert.Equal(0, usage.GetProperty("toolCalls").GetInt64());
                cursor = await running.Cursor("needs_me");
            }
            await using (var reopened = await Host.Start(host, home, fake))
            {
                using var socket = await reopened.Open();
                using var bulk = await reopened.Open(bulk: true);
                Assert.Equal(machineBefore, (await Entity(bulk, "session", "owner")).GetProperty("machine").GetString());
                Assert.Equal(answerBefore, (await Entity(bulk, "needs_me", "need")).GetRawText());
                var receipt = await Call(socket, "command", JsonSerializer.SerializeToElement(answer, EngineSocketJson.Default.WindowCommand));
                Assert.True(receipt.GetProperty("applied").GetBoolean(), receipt.ToString());
                using var replay = await reopened.Open();
                await Send(replay, new { t = "sub", id = 7, stream = "engine", topic = "needs_me", after = cursor });
                var page = await Read(replay);
                Assert.Equal("engine_events", page.GetProperty("t").GetString());
                Assert.Equal(cursor, page.GetProperty("after").GetInt64());
                Assert.Empty(page.GetProperty("events").EnumerateArray());
                // A fresh effect is a worker completion gate after restart, so replay assertions cannot race startup.
                await Command(socket, "after-restart", new SendMessage(null, "owner", "Gate", "prompt"));
                await reopened.WaitState("messages", "after-restart", "delivered_to_turn");
                Assert.Single(fake.AnsweredRequestIds);
                Assert.Empty(fake.UnexpectedReplays);
            }
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Host_lifecycle_releases_home_and_never_fakes_success(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-lifecycle-").FullName;
        try
        {
            await using (var noHandler = await Host.Start(host, home, null))
            {
                using var socket = await noHandler.Open();
                using var bulk = await noHandler.Open(bulk: true);
                await Command(socket, "pending", new StartSession("codex", "model", home, "Pending", null, null, null));
                Assert.Equal("starting", (await Entity(bulk, "session", "pending")).GetProperty("state").GetString());
            }
            var blocked = new HostEffects { Block = true };
            var running = await Host.Start(host, home, blocked);
            try
            {
                var intent = await blocked.Entered.Task.WaitAsync(Timeout);
                Assert.Equal(1, intent.Attempt); // No handler left the start pending, never claimed or completed.
            }
            finally { await running.DisposeAsync(); }
            Assert.True(blocked.Joined.Task.IsCompletedSuccessfully);
            await using var reopened = await Host.Start(host, home, null);
            using var bulkAgain = await reopened.Open(bulk: true);
            Assert.Equal("starting", (await Entity(bulkAgain, "session", "pending")).GetProperty("state").GetString());
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Failed_startup_releases_home_and_joins_engine(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-failed-").FullName;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Host.Start(host, home, null, failStarted: true));
            await using var reopened = await Host.Start(host, home, null);
            using var socket = await reopened.Open();
            await Command(socket, "s", new StartSession("codex", "model", home, "After failure", null, null, null));
        }
        finally { Directory.Delete(home, true); }
    }

    private sealed class HostEffects : IEngineEffects
    {
        public EngineRuntime Runtime { get; set; } = null!;
        public bool Block { get; init; }
        public TaskCompletionSource<Effect> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> AnsweredRequestIds { get; } = [];
        public List<string> UnexpectedReplays { get; } = [];
        private readonly HashSet<string> _successful = [];

        public void Submit(string id, EngineCommand command, long version = 0, EngineActor? actor = null)
        {
            var result = Runtime.Submit(new(Guid.NewGuid(), Runtime.Machine, actor ?? new(ActorKind.System, "adapter"),
                id, version, 0, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()), command);
            Assert.True(result.Applied, result.Error);
        }

        public async Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(effect);
            if (Block)
            {
                try { await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken); }
                finally { Joined.TrySetResult(); }
            }
            if (!_successful.Add(effect.Id)) UnexpectedReplays.Add(effect.Id);
            JsonElement receipt;
            switch (effect.Kind)
            {
                case "start":
                    receipt = JsonSerializer.SerializeToElement(new SessionEffectData("idle", "provider-session", null), EngineJson.Default.SessionEffectData);
                    break;
                case "answer":
                    var request = effect.Payload.GetProperty("providerRequestId").GetString()!;
                    AnsweredRequestIds.Add(request);
                    receipt = JsonSerializer.SerializeToElement(new AnswerEffectData(request), EngineJson.Default.AnswerEffectData);
                    break;
                case "send":
                    receipt = JsonSerializer.SerializeToElement(new MessageEffectData("delivered_to_turn", "turn", null), EngineJson.Default.MessageEffectData);
                    break;
                default: throw new InvalidOperationException(effect.Kind);
            }
            return new(EffectDisposition.Confirmed, receipt, null);
        }

        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected reconciliation: " + effect.Id);
    }

    private sealed class Host : IAsyncDisposable
    {
        private DesktopBackend? _desktop;
        private Task<int>? _remote;
        private readonly CancellationTokenSource _stop = new();
        private Uri _address = null!;

        public static async Task<Host> Start(string host, string home, HostEffects? effects, bool failStarted = false)
        {
            var result = new Host();
            void Started(EngineRuntime runtime)
            {
                if (effects is not null) effects.Runtime = runtime;
                if (failStarted) throw new InvalidOperationException("injected startup failure");
            }
            try
            {
                if (host == "desktop")
                {
                    result._desktop = await DesktopBackend.StartAsync(home, "engine-token", 0, new FakeLauncher(), TextWriter.Null,
                        engineEffects: effects, engineStarted: Started);
                    result._address = result._desktop.Address;
                }
                else
                {
                    var tokenFile = Path.Combine(home, "token");
                    await File.WriteAllTextAsync(tokenFile, "ASSEMBLY_TOKEN=engine-token\n");
                    var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
                    result._remote = ServeRun.RunAsync(new(0, home, tokenFile, Path.Combine(home, "agents.json")),
                        new FakeLauncher(), effects, (address, runtime) => { Started(runtime); ready.TrySetResult(address); }, result._stop.Token);
                    var completed = await Task.WhenAny(ready.Task, result._remote).WaitAsync(Timeout);
                    if (completed == result._remote) await result._remote;
                    result._address = await ready.Task.WaitAsync(Timeout);
                }
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        public async Task<ClientWebSocket> Open(bool bulk = false)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://{_address.Authority}/socket{(bulk ? "/bulk" : "")}"), CancellationToken.None);
            await Send(socket, new { t = "hello", token = "engine-token", protocol = 1 });
            Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
            return socket;
        }

        public async Task WaitState(string topic, string id, string state)
        {
            using var socket = await Open();
            await Send(socket, new { t = "sub", id = 7, stream = "engine", topic, key = id });
            while (true)
            {
                var page = await Read(socket);
                var snapshot = page.GetProperty("t").GetString() == "engine_snapshot";
                var items = page.GetProperty(snapshot ? "items" : "events").EnumerateArray();
                if (items.Any(item => snapshot
                    ? item.GetProperty("id").GetString() == id && item.GetProperty("state").GetString() == state
                    : item.GetProperty("entityId").GetString() == id && item.GetProperty("data").GetProperty("state").GetString() == state)) return;
            }
        }

        public async Task<List<string>> SnapshotIds(string topic)
        {
            using var socket = await Open();
            await Send(socket, new { t = "sub", id = 7, stream = "engine", topic });
            var page = await Read(socket);
            Assert.False(page.GetProperty("more").GetBoolean());
            return page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
        }

        public async Task<long> Cursor(string topic)
        {
            using var socket = await Open();
            await Send(socket, new { t = "sub", id = 7, stream = "engine", topic });
            return (await Read(socket)).GetProperty("through").GetInt64();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                if (_desktop is not null) await _desktop.DisposeAsync();
                if (_remote is not null) await _remote.WaitAsync(Timeout);
            }
            finally { _stop.Dispose(); }
        }
    }

    private static async Task Command(ClientWebSocket socket, string id, EngineCommand command)
    {
        var result = await Call(socket, "command", JsonSerializer.SerializeToElement(new WindowCommand(Guid.NewGuid(), id, 0, 0, command), EngineSocketJson.Default.WindowCommand));
        Assert.True(result.GetProperty("applied").GetBoolean(), result.ToString());
    }

    private static Task<JsonElement> Entity(ClientWebSocket socket, string kind, string id) => Call(socket, "entity", new { entityKind = kind, entityId = id });

    private static async Task<JsonElement> Call(ClientWebSocket socket, string method, object args)
    {
        await Send(socket, new { t = "call", id = 1, feature = "engine", method, args });
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Clone();
    }

    private static Task Send(ClientWebSocket socket, object frame) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, part.MessageType);
            bytes.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var doc = JsonDocument.Parse(bytes.ToArray());
        return doc.RootElement.Clone();
    }
}
