using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Recording;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class EngineForwardTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly EngineActor Adapter = new(ActorKind.System, "test-adapter");

    [Fact]
    public async Task Answer_from_the_mac_reaches_the_remote_need_once()
    {
        var effects = new Answers();
        await using var h = await Started(effects);
        var before = LocalState(h);
        using var socket = await Open(h.Mac);
        var request = Command(1, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("answered", Json("allow")), 1);
        await Send(socket, request);
        var result = Result(await Read(socket));
        Assert.True(result.Applied);
        await h.Reached(result.Seq);
        Assert.Equal("answered", h.Mac.Runtime.Reader.Entity("needs_me", "n", default, h.Remote.Fixture.Machine)!.Value.GetProperty("state").GetString());
        Assert.Equal("request-7", await effects.Answered.Task.WaitAsync(Wait));
        await Send(socket, request);
        Assert.Equal(result, Result(await Read(socket)));
        Assert.Single(effects.Requests);
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE kind='needs_me.answered'"));
        Assert.Equal(before, LocalState(h));
    }

    [Fact]
    public async Task Retry_after_a_dropped_response_replays_the_owner_receipt_and_conflict()
    {
        var effects = new Answers();
        await using var h = await Started(effects);
        var before = LocalState(h);
        var id = Guid.NewGuid();
        var request = Command(1, h.Remote.Fixture.Machine, id, new ResolveNeed("answered", Json("allow")), 1);
        var dropped = 0;
        h.Remote.CommandCommitted = () => { if (Interlocked.Exchange(ref dropped, 1) == 0) h.Remote.Endpoint.DropAll(); };
        using var socket = await Open(h.Mac);
        await Send(socket, request);
        var error = await Read(socket);
        Assert.Equal("unreachable", error.GetProperty("code").GetString());
        Assert.Equal("workbox-test", error.GetProperty("machine").GetString());
        var saved = h.Remote.Fixture.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", id.ToString("N")));
        var committed = JsonSerializer.Deserialize(saved!, EngineJson.Default.CommandResult)!;
        Assert.True(committed.Applied);
        await h.Reached(h.Remote.Through);
        await Send(socket, request);
        Assert.Equal(committed, Result(await Read(socket)));
        await Send(socket, Command(2, h.Remote.Fixture.Machine, id, new ResolveNeed("dismissed", null), 1));
        Assert.Equal("command_id_conflict", Result(await Read(socket)).Error);
        Assert.Equal("request-7", await effects.Answered.Task.WaitAsync(Wait));
        Assert.Single(effects.Requests);
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE kind='needs_me.answered'"));
        Assert.Equal(before, LocalState(h));
    }

    [Fact]
    public async Task Disconnected_known_machine_is_unreachable_and_unknown_or_retired_is_not_found()
    {
        await using var h = await Started();
        using var socket = await Open(h.Mac);
        var before = LocalState(h);
        var remoteBefore = h.Remote.Through;
        await h.DisconnectMirror();
        await Send(socket, Command(1, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 1));
        var error = await Read(socket);
        Assert.Equal("unreachable", error.GetProperty("code").GetString());
        Assert.Equal("workbox-test", error.GetProperty("machine").GetString());
        await Send(socket, Command(2, "unknown", Guid.NewGuid(), new ResolveNeed("dismissed", null), 1));
        Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        h.Mac.Fixture.Db.Writer.Write("test.retire", s => s.Execute("UPDATE engine_mirror SET state='retired'"));
        await Send(socket, Command(3, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 1));
        Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        Assert.Equal(before, LocalState(h));
        Assert.Equal(remoteBefore, h.Remote.Through);
    }

    [Fact]
    public async Task A_hung_remote_response_keeps_its_commands_ordered_and_does_not_delay_local_commands()
    {
        await using var h = await Started();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.CommandCommitted = () => { entered.TrySetResult(); release.Task.WaitAsync(Wait).GetAwaiter().GetResult(); };
        using var socket = await Open(h.Mac);
        try
        {
            await Send(socket, Command(1, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 1));
            await entered.Task.WaitAsync(Wait);
            await Send(socket, Command(2, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 2));
            await Send(socket, Command(3, null, Guid.NewGuid(), new StartSession("codex", "model", "/test", "Local", null, null, null), entity: "local"));
            var local = await Read(socket);
            Assert.Equal(3, local.GetProperty("id").GetInt64());
            Assert.True(Result(local).Applied);
            Assert.Equal("starting", h.Mac.Fixture.State("engine_session", "local"));
            release.TrySetResult();
            Assert.True(Result(await Read(socket)).Applied);
            Assert.Equal("already_resolved", Result(await Read(socket)).Error);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Tool_preview_and_usage_come_from_the_owner_while_display_entities_stay_mirrored()
    {
        await using var h = await Started();
        var a = new Attribution("s", null, null, null, 0);
        Assert.True(h.Remote.Fixture.Apply(new ToolStarted(a, "provider", "call", "read", Json(new { path = "remote.txt" }), 42), "tool", actor: Adapter).Applied);
        Assert.True(h.Remote.Fixture.Apply(new ToolEnded(a, "succeeded", Json(new { text = "remote-output" }), 43), "tool", actor: Adapter).Applied);
        Assert.True(h.Remote.Fixture.Apply(new TurnUsage(a, "turn", 123, 45, 6, 789, 12), "usage", actor: Adapter).Applied);
        await h.Reached(h.Remote.Through);
        var before = LocalState(h);
        using var bulk = await Open(h.Mac, true);
        await Send(bulk, Call(1, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
        var row = (await Read(bulk)).GetProperty("result");
        Assert.Equal(h.Remote.Fixture.Machine, row.GetProperty("machine").GetString());
        Assert.Equal(h.Remote.Fixture.Text("SELECT input_preview FROM engine_tool_call WHERE id='tool'"), row.GetProperty("inputPreview").GetString());
        Assert.Equal(h.Remote.Fixture.Text("SELECT output_preview FROM engine_tool_call WHERE id='tool'"), row.GetProperty("outputPreview").GetString());
        Assert.Equal(42, row.GetProperty("inputBytes").GetInt64());
        Assert.Equal(43, row.GetProperty("outputBytes").GetInt64());
        using var live = await Open(h.Mac);
        await Send(live, Call(2, "usage", "session", "s", h.Remote.Fixture.Machine));
        var totals = (await Read(live)).GetProperty("result");
        Assert.Equal(123, totals.GetProperty("inputTokens").GetInt64());
        Assert.Equal(789, totals.GetProperty("costNanodollars").GetInt64());
        Assert.Equal(1, totals.GetProperty("toolCalls").GetInt64());
        await Send(bulk, Call(6, "entity", "usage", "usage", h.Remote.Fixture.Machine));
        var usage = (await Read(bulk)).GetProperty("result");
        Assert.Equal(h.Remote.Fixture.Machine, usage.GetProperty("machine").GetString());
        Assert.Equal(123, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0, h.Mac.Fixture.Count("engine_tool_call"));
        Assert.Equal(0, h.Mac.Fixture.Count("engine_usage"));
        await h.DisconnectMirror();
        await Send(bulk, Call(3, "entity", "needs_me", "n", h.Remote.Fixture.Machine));
        Assert.Equal("open", (await Read(bulk)).GetProperty("result").GetProperty("state").GetString());
        await Send(live, Call(4, "usage", "session", "s", h.Remote.Fixture.Machine));
        Assert.Equal("unreachable", (await Read(live)).GetProperty("code").GetString());
        await Send(bulk, Call(5, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
        Assert.Equal("unreachable", (await Read(bulk)).GetProperty("code").GetString());
        Assert.Equal(before, LocalState(h));
    }

    [Fact]
    public async Task A_closed_owner_bulk_socket_returns_unreachable_without_local_engine_writes()
    {
        await using var h = await Started();
        using var bulk = await Open(h.Mac, true);
        var before = LocalState(h);
        await h.Remote.Stop();
        await Send(bulk, Call(1, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
        var error = await Read(bulk);
        Assert.Equal("unreachable", error.GetProperty("code").GetString());
        Assert.Equal("workbox-test", error.GetProperty("machine").GetString());
        Assert.Equal(before, LocalState(h));
    }

    [Fact]
    public async Task Both_window_doors_enforce_the_allowlist_and_forward_exact_version_and_epoch()
    {
        await using var h = await Started();
        using var mac = await Open(h.Mac);
        using var owner = await Open(h.Remote);
        var before = LocalState(h);
        var remoteBefore = h.Remote.Fixture.Count("engine_command_receipt");
        var forbidden = new OpenNeed("s", null, null, "permission", "no", [], "other", null, null);
        await Send(mac, Command(1, h.Remote.Fixture.Machine, Guid.NewGuid(), forbidden));
        Assert.Equal("forbidden", Result(await Read(mac)).Error);
        await Send(owner, Command(1, null, Guid.NewGuid(), forbidden));
        Assert.Equal("forbidden", Result(await Read(owner)).Error);
        Assert.Equal(remoteBefore, h.Remote.Fixture.Count("engine_command_receipt"));
        await Send(mac, Command(2, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 0));
        Assert.Equal("version_conflict", Result(await Read(mac)).Error);
        await Send(mac, Command(3, h.Remote.Fixture.Machine, Guid.NewGuid(), new ResolveNeed("dismissed", null), 1, 7));
        Assert.Equal("stale_epoch", Result(await Read(mac)).Error);
        await Send(mac, Command(4, h.Remote.Fixture.Machine, Guid.Empty, new ResolveNeed("dismissed", null), 1));
        Assert.Equal("bad_args", Result(await Read(mac)).Error);
        Assert.Equal("open", h.Remote.Fixture.State("engine_needs_me", "n"));
        Assert.Equal(before, LocalState(h));
    }

    [Fact]
    public async Task Explicit_local_machine_uses_the_existing_local_command_path()
    {
        await using var h = await Started();
        using var socket = await Open(h.Mac);
        var remoteBefore = h.Remote.Through;
        await Send(socket, Command(1, h.Mac.Fixture.Machine, Guid.NewGuid(),
            new StartSession("codex", "model", "/test", "Here", null, null, null), entity: "here"));
        Assert.True(Result(await Read(socket)).Applied);
        Assert.Equal("starting", h.Mac.Fixture.State("engine_session", "here"));
        Assert.Equal(h.Mac.Fixture.Machine, h.Mac.Fixture.Text("SELECT machine FROM engine_session WHERE id='here'"));
        Assert.Equal(remoteBefore, h.Remote.Through);
    }

    private static async Task<TwoHosts> Started(IEngineEffects? effects = null)
    {
        var h = new TwoHosts(windowCalls: true, forwardCommands: true, effects: effects);
        try
        {
            await h.Start();
            Assert.True(h.Remote.Fixture.Apply(new OpenNeed("s", null, null, "permission", "Allow?", [], "request-7", null, null), "n", actor: Adapter).Applied);
            await h.StartMirror();
            return h;
        }
        catch { await h.DisposeAsync(); throw; }
    }

    private static (long Receipts, long Next, long Needs, long Outbox) LocalState(TwoHosts h) =>
        (h.Mac.Fixture.Count("engine_command_receipt"), h.Mac.Fixture.Number("SELECT next_seq FROM engine_meta"),
            h.Mac.Fixture.Count("engine_needs_me"), h.Mac.Fixture.Count("engine_outbox"));
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static CommandResult Result(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!;
    }
    private static string Command(long id, string? machine, Guid commandId, EngineCommand command, long version = 0, long epoch = 0, string entity = "n")
    {
        var json = JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand);
        var source = machine is null ? "" : ",\"machine\":" + JsonSerializer.Serialize(machine, EngineJson.Default.String);
        return $$$"""{"t":"call","id":{{{id}}},"feature":"engine","method":"command","args":{"commandId":"{{{commandId}}}","entityId":"{{{entity}}}","expectedVersion":{{{version}}},"epoch":{{{epoch}}},"command":{{{json}}}{{{source}}}}}""";
    }
    private static string Call(long id, string method, string kind, string entity, string machine) =>
        $$$"""{"t":"call","id":{{{id}}},"feature":"engine","method":"{{{method}}}","args":{"entityKind":"{{{kind}}}","entityId":"{{{entity}}}","machine":"{{{machine}}}"}}""";
    private static async Task<ClientWebSocket> Open(TwoHosts.Host host, bool bulk = false)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(host.Address) { Scheme = "ws", Path = bulk ? "/socket/bulk" : "/socket" }.Uri, default);
        await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        return socket;
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream();
        var bytes = new byte[65536]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(bytes, timeout.Token); Assert.Equal(WebSocketMessageType.Text, result.MessageType); buffer.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
    private sealed class Answers : IEngineEffects
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource<string> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct)
        {
            var request = effect.Payload.Deserialize(EngineJson.Default.NeedAnswer)!.ProviderRequestId;
            Requests.Enqueue(request); Answered.TrySetResult(request);
            return Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new AnswerEffectData(request), EngineJson.Default.AnswerEffectData), null));
        }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct) => ExecuteAsync(effect, ct);
    }
}
