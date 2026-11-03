using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class ChatExpectedSourceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Local_commands_keep_owner_prompt_once_run_membership_and_existing_provider_operations()
    {
        await using var hosts = new TwoHosts(windowCalls: true);
        Installed? installed = null;
        hosts.Mac.ChatDoor = new DeferredReads(() => installed?.Door);
        hosts.Mac.AdditionalFeatures = calls => installed = Install(hosts.Mac, calls);
        await hosts.Start(); Seed(hosts.Mac, "selected"); Seed(hosts.Mac, "other");
        var source = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        using var socket = await Open(hosts.Mac.Address);
        var prompt = Args(new SendFeature.SendArgs("selected", "Exact text", "client", ExpectedSource: source), SessionsJson.Default.SendArgs);
        var first = Result(await Call(socket, 1, "send", "send", prompt));
        var replay = Result(await Call(socket, 2, "send", "send",
            Args(new SendFeature.SendArgs("selected", "Exact text", "client"), SessionsJson.Default.SendArgs), source));
        Assert.True(JsonElement.DeepEquals(first, replay));
        Assert.Equal("queued", first.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, first.GetProperty("runId").ValueKind);
        Assert.Equal(1, hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_message WHERE to_session_id='selected'"));

        // SOURCE-DERIVED current rows provide the same run-membership proof used by the existing stop operation.
        hosts.Mac.NeedHub.Publish("other", new RowConverter().Apply(new TurnStarted(1000, "other-run", "Other turn")));
        Error(await Call(socket, 3, "send", "stop", Args(new SendFeature.StopArgs("other-run", "selected", source), SessionsJson.Default.StopArgs)), "failed");
        Assert.DoesNotContain("stop other-run", installed!.Machine.Asked);
        hosts.Mac.NeedHub.Publish("selected", new RowConverter().Apply(new TurnStarted(1001, "selected-run", "Selected turn")));
        Result(await Call(socket, 4, "send", "stop", Args(new SendFeature.StopArgs("selected-run", "selected", source), SessionsJson.Default.StopArgs)));
        Result(await Call(socket, 5, "send", "stopTask", Args(new SendFeature.StopTaskArgs("selected", "task", source), SessionsJson.Default.StopTaskArgs)));
        Result(await Call(socket, 6, "config", "get", Args(new ConfigFeature.GetArgs("selected"), SessionsJson.Default.GetArgs), source));
        Result(await Call(socket, 7, "config", "warm", Args(new ConfigFeature.WarmArgs("selected", "target", ExpectedSource: source), SessionsJson.Default.WarmArgs)));
        Result(await Call(socket, 8, "config", "set", Args(new ConfigFeature.SetArgs("selected", "model", "choice", ExpectedSource: source), SessionsJson.Default.SetArgs)));
        Error(await Call(socket, 9, "send", "answer", Args(new SendFeature.AnswerArgs("selected", "not-open", Element("{}"), source), SessionsJson.Default.AnswerArgs)), "failed");
        Assert.Contains("stop selected-run", installed.Machine.Asked);
        Assert.Contains("stop task task", installed.Machine.Asked); Assert.Contains("config", installed.Machine.Asked);
        Assert.Contains("warm config target", installed.Machine.Asked); Assert.Contains("set config model choice", installed.Machine.Asked);
        Assert.Equal(0, hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        await PendingAndMessage(hosts.Mac.Address, socket, source, "Exact text");
    }

    [Fact]
    public async Task Contradictory_or_stale_sources_refuse_before_lookup_provider_or_owner_admission()
    {
        await using var hosts = new TwoHosts(); Installed? installed = null;
        hosts.Mac.AdditionalFeatures = calls => installed = Install(hosts.Mac, calls);
        await hosts.Start(); Seed(hosts.Mac, "selected");
        using var socket = await Open(hosts.Mac.Address);
        var source = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        var prompt = Args(new SendFeature.SendArgs("does-not-exist", "Text", "client", ExpectedSource: source), SessionsJson.Default.SendArgs);
        Error(await Call(socket, 1, "send", "send", prompt, source with { Generation = 1 }), "bad_args");
        var stale = source with { Machine = "different-actual-owner" };
        Error(await Call(socket, 2, "send", "send", Args(new SendFeature.SendArgs("selected", "Text", "client", ExpectedSource: stale), SessionsJson.Default.SendArgs)), "source_changed");
        Error(await Call(socket, 3, "config", "get", Args(new ConfigFeature.GetArgs("selected", stale), SessionsJson.Default.GetArgs)), "source_changed");
        Error(await Call(socket, 4, "send", "stop", Args(new SendFeature.StopArgs("unknown", ExpectedSource: source), SessionsJson.Default.StopArgs)), "bad_args");
        Assert.Empty(installed!.Machine.Asked); Assert.Equal(0, hosts.Mac.Fixture.Count("engine_message"));
    }

    [Fact]
    public async Task Missing_source_gateway_refuses_while_legacy_config_and_omitted_wire_fields_remain_supported()
    {
        await using var hosts = new TwoHosts(); FakeMachine? machine = null;
        hosts.Mac.AdditionalFeatures = calls =>
        {
            machine = new(null, hosts.Mac.Sessions);
            new ConfigFeature(hosts.Mac.Sessions, new MachineSet(machine)).Register(calls);
        };
        await hosts.Start(); Seed(hosts.Mac, "selected"); using var socket = await Open(hosts.Mac.Address);
        var source = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        Error(await Call(socket, 1, "config", "get", Args(new ConfigFeature.GetArgs("selected", source), SessionsJson.Default.GetArgs)), "source_changed");
        Assert.Empty(machine!.Asked);
        var legacy = Args(new ConfigFeature.GetArgs("selected"), SessionsJson.Default.GetArgs);
        Assert.False(legacy.TryGetProperty("expectedSource", out _));
        Result(await Call(socket, 2, "config", "get", legacy)); Assert.Equal(["config"], machine.Asked);
        var stop = Args(new SendFeature.StopArgs("run"), SessionsJson.Default.StopArgs);
        Assert.False(stop.TryGetProperty("expectedSource", out _)); Assert.False(stop.TryGetProperty("session", out _));
        await Send(socket, $$"""{"t":"sub","id":99,"stream":"engine","topic":"messages","key":"missing","machine":{{JsonSerializer.Serialize(source.Machine, RowJsonContext.Rows.String)}},"expectedSource":{{JsonSerializer.Serialize(source, RowJsonContext.Rows.ExpectedSource)}}}""");
        Error(await Read(socket), "source_changed");
    }

    [Fact]
    public async Task Writer_certification_refuses_a_changed_facade_and_rolls_back_without_owner_admission()
    {
        await using var hosts = new TwoHosts();
        hosts.Mac.AdditionalFeatures = calls => Install(hosts.Mac, calls, decorate: actual => new MovedAtCommit(actual));
        await hosts.Start(); Seed(hosts.Mac, "selected"); using var socket = await Open(hosts.Mac.Address);
        var source = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        Error(await Call(socket, 1, "send", "send", Args(new SendFeature.SendArgs("selected", "Text", "client", ExpectedSource: source), SessionsJson.Default.SendArgs)), "source_changed");
        Assert.Null(hosts.Mac.Sessions.Get("selected")!.Machine);
        Assert.Equal(0, hosts.Mac.Fixture.Count("engine_message"));
        Assert.Equal(0, hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM audit WHERE what='send/send'"));
    }

    [Fact]
    public async Task Remote_commands_use_one_actual_owner_and_never_the_poisoned_local_provider_or_run_copy()
    {
        await using var f = await RemoteFixture.Start(); using var socket = await Open(f.Hosts.Mac.Address);
        var reply = Result(await Call(socket, 1, "config", "get", Args(new ConfigFeature.GetArgs("selected", f.Source), SessionsJson.Default.GetArgs)));
        Assert.Contains("OWNER", reply.GetRawText()); Assert.DoesNotContain("POISON", reply.GetRawText());
        var prompt = Args(new SendFeature.SendArgs("selected", "Exact remote text", "client", ExpectedSource: f.Source), SessionsJson.Default.SendArgs);
        var accepted = Result(await Call(socket, 2, "send", "send", prompt));
        Assert.Equal("queued", accepted.GetProperty("state").GetString());
        Assert.Equal(1, f.Hosts.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_message WHERE to_session_id='selected'"));
        Assert.Equal(0, f.Hosts.Mac.Fixture.Count("engine_message"));
        f.Hosts.Remote.NeedHub.Publish("selected", new RowConverter().Apply(new TurnStarted(1001, "actual-owner-run", "Owner turn")));
        Result(await Call(socket, 3, "send", "stop", Args(new SendFeature.StopArgs("actual-owner-run", "selected", f.Source), SessionsJson.Default.StopArgs)));
        Result(await Call(socket, 4, "send", "stopTask", Args(new SendFeature.StopTaskArgs("selected", "task", f.Source), SessionsJson.Default.StopTaskArgs)));
        Result(await Call(socket, 5, "config", "set", Args(new ConfigFeature.SetArgs("selected", "model", "choice", ExpectedSource: f.Source), SessionsJson.Default.SetArgs)));
        Error(await Call(socket, 6, "send", "answer", Args(new SendFeature.AnswerArgs("selected", "closed", Element("{}"), f.Source), SessionsJson.Default.AnswerArgs)), "failed");
        Error(await Call(socket, 7, "config", "warm", Args(new ConfigFeature.WarmArgs("selected", "unqualified-target", ExpectedSource: f.Source), SessionsJson.Default.WarmArgs)), "failed");
        Assert.DoesNotContain(f.Owner.Machine.Asked, asked => asked.StartsWith("warm config", StringComparison.Ordinal));
        Assert.Empty(f.Local.Machine.Asked);
        Assert.Contains("stop actual-owner-run", f.Owner.Machine.Asked);
        Assert.Contains("stop task task", f.Owner.Machine.Asked); Assert.Contains("set config model choice", f.Owner.Machine.Asked);
        await PendingAndMessage(f.Hosts.Mac.Address, socket, f.Source, "Exact remote text");
    }

    [Fact]
    public async Task Actual_remote_invalidation_during_provider_read_returns_no_data_or_retargeted_effect()
    {
        await using var f = await RemoteFixture.Start(); using var socket = await Open(f.Hosts.Mac.Address);
        Result(await Call(socket, 2, "send", "send", Args(new SendFeature.SendArgs("selected", "Message stream", "client", ExpectedSource: f.Source), SessionsJson.Default.SendArgs)));
        await Message(socket, f.Source, "prompt/selected/client");
        f.Owner.Machine.Hold();
        await SendCall(socket, 1, "config", "get", Args(new ConfigFeature.GetArgs("selected", f.Source), SessionsJson.Default.GetArgs));
        await f.Owner.Machine.Reached.Task.WaitAsync(Wait);
        await f.Hosts.DisconnectMirror();
        // A result on the same real sender drains anything legitimately sent before invalidation.
        await SendCall(socket, 100, "engine", "machine", Element("{}"));
        List<JsonElement> frames = [];
        while (!frames.Any(frame => Id(frame) == 100) || !frames.Any(frame => Id(frame) == 1) || !frames.Any(frame => Id(frame) == 99 && frame.GetProperty("t").GetString() == "error"))
            frames.Add(await Read(socket));
        var refused = Assert.Single(frames, frame => Id(frame) == 1);
        Assert.Equal("error", refused.GetProperty("t").GetString()); Assert.False(refused.TryGetProperty("result", out _));
        var invalidated = frames.FindIndex(frame => Id(frame) == 99 && frame.GetProperty("t").GetString() == "error");
        Error(frames[invalidated], "source_changed");
        Assert.DoesNotContain(frames.Skip(invalidated + 1), frame => Id(frame) == 99 && frame.GetProperty("t").GetString() is "engine_snapshot" or "engine_events");
        await Send(socket, "{\"t\":\"ack\",\"id\":99,\"stream\":\"engine\",\"after\":0}");
        await Send(socket, "{\"t\":\"unsub\",\"id\":99}");
        await f.Owner.Machine.Cancelled.Task.WaitAsync(Wait); Assert.Empty(f.Local.Machine.Asked);
        Assert.Equal(0, f.Hosts.Mac.Fixture.Count("engine_message"));
    }

    private sealed record Installed(FakeMachine Machine, IEngineDoor Door, MachineSet Machines, SessionRowSources Sources);
    private static Installed Install(TwoHosts.Host host, ICallRegistry calls, RemoteConnections? connections = null,
        Func<IEngineDoor, IEngineDoor>? decorate = null)
    {
        var installed = Build(host, connections, decorate);
        Register(host, calls, installed); return installed;
    }
    private static Installed Build(TwoHosts.Host host, RemoteConnections? connections = null, Func<IEngineDoor, IEngineDoor>? decorate = null)
    {
        var machine = new FakeMachine(null, host.Sessions);
        var machines = new MachineSet(machine);
        var sources = new SessionRowSources(host.Fixture.Db, host.Sessions, machines, connections);
        IEngineDoor door = new EngineDoor(host.Runtime, host.Sessions, machines,
            new AttachmentStore(Path.Combine(Path.GetTempPath(), "source-chat-" + Guid.NewGuid().ToString("N"))),
            host.Fixture.Time, host.Needs, host.NeedHub, sources);
        if (decorate is not null) door = decorate(door);
        return new(machine, door, machines, sources);
    }
    private static void Register(TwoHosts.Host host, ICallRegistry calls, Installed installed)
    {
        new SendFeature(host.Sessions, installed.Machines, host.NeedHub, installed.Door).Register(calls);
        new ConfigFeature(host.Sessions, installed.Machines, installed.Door).Register(calls);
        new RowsFeature(host.NeedHub, RowStore.Open(host.Fixture.Db), host.Sessions, installed.Machines, installed.Sources).Register(calls);
    }

    private sealed class RemoteFixture : IAsyncDisposable
    {
        internal TwoHosts Hosts { get; } = new(windowCalls: true, forwardCommands: true);
        internal Installed Owner = null!, Local = null!;
        internal ExpectedSource Source = null!;
        internal static async Task<RemoteFixture> Start()
        {
            var f = new RemoteFixture(); ICallRegistry? localCalls = null;
            f.Owner = Build(f.Hosts.Remote); f.Hosts.Remote.ChatDoor = f.Owner.Door;
            f.Hosts.Remote.AdditionalFeatures = calls => Register(f.Hosts.Remote, calls, f.Owner);
            f.Hosts.Mac.ChatDoor = new DeferredReads(() => f.Local?.Door);
            f.Hosts.Mac.AdditionalFeatures = calls => localCalls = calls;
            try
            {
                await f.Hosts.Start(); Seed(f.Hosts.Remote, "selected");
                f.Hosts.Mac.Sessions.Add("selected", "codex", "source-derived", "workbox-test");
                await f.Hosts.StartMirror();
                f.Local = Install(f.Hosts.Mac, localCalls!, f.Hosts.Connections);
                f.Owner.Machine.Config = new("[]", new Dictionary<string, string> { ["model"] = "OWNER" });
                f.Local.Machine.Config = new("[]", new Dictionary<string, string> { ["model"] = "POISON" });
                f.Source = f.Hosts.Mac.Fixture.Db.QueryFirst("""
                    SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
                    """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default,
                    new SqlArg("$machine", f.Hosts.Remote.Fixture.Machine))!;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { Owner?.Machine.Release(); await Hosts.DisposeAsync(); }
    }

    // This wrapper delegates real preparation/admission, changing identity only inside the supplied real writer.
    private sealed class MovedAtCommit(IEngineDoor actual) : IEngineDoor
    {
        public async Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareSourceOwnerPromptAsync(OwnerPromptRequest request, CallContext call)
        {
            var apply = await actual.PrepareSourceOwnerPromptAsync(request, call);
            return scope => { scope.Execute("UPDATE session_info SET machine='different-route' WHERE session_id=$id", new SqlArg("$id", request.Session)); return apply(scope); };
        }
        public Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(OwnerPromptRequest r, CancellationToken ct) => actual.PrepareOwnerPromptAsync(r, ct);
        public Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(NeedAnswerRequest r, CancellationToken ct) => actual.PrepareNeedAnswerAsync(r, ct);
        public Task<SessionTitleStat> ReadSessionTitleAsync(SessionTitleReadArgs r, CancellationToken ct) => actual.ReadSessionTitleAsync(r, ct);
        public Task<SessionTitleChunk> ReadSessionTitleChunkAsync(SessionTitleChunkArgs r, CancellationToken ct) => actual.ReadSessionTitleChunkAsync(r, ct);
        public Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs r, CancellationToken ct) => actual.ReadOwnerPromptsAsync(r, ct);
        public Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs r, CancellationToken ct) => actual.ReadOwnerPromptChunkAsync(r, ct);
    }

    private sealed class DeferredReads(Func<IEngineDoor?> current) : IEngineDoor
    {
        private IEngineDoor Actual => current() ?? throw new CallFailed("Source gateway unavailable.", "source_changed");
        public Task<OwnerPromptPage> ReadSourceOwnerPromptsAsync(OwnerPromptReadArgs r, CallContext c) => Actual.ReadSourceOwnerPromptsAsync(r, c);
        public Task<OwnerPromptChunk> ReadSourceOwnerPromptChunkAsync(OwnerPromptChunkArgs r, CallContext c) => Actual.ReadSourceOwnerPromptChunkAsync(r, c);
        public Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(OwnerPromptRequest r, CancellationToken ct) => Actual.PrepareOwnerPromptAsync(r, ct);
        public Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(NeedAnswerRequest r, CancellationToken ct) => Actual.PrepareNeedAnswerAsync(r, ct);
        public Task<SessionTitleStat> ReadSessionTitleAsync(SessionTitleReadArgs r, CancellationToken ct) => Actual.ReadSessionTitleAsync(r, ct);
        public Task<SessionTitleChunk> ReadSessionTitleChunkAsync(SessionTitleChunkArgs r, CancellationToken ct) => Actual.ReadSessionTitleChunkAsync(r, ct);
        public Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs r, CancellationToken ct) => Actual.ReadOwnerPromptsAsync(r, ct);
        public Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs r, CancellationToken ct) => Actual.ReadOwnerPromptChunkAsync(r, ct);
    }

    private static async Task PendingAndMessage(Uri address, WebSocket control, ExpectedSource source, string text)
    {
        var page = Result(await Call(control, 20, "engine", "owner_prompts", Args(new OwnerPromptReadArgs("selected", source.Machine, ExpectedSource: source), EngineSocketJson.Default.OwnerPromptReadArgs)))
            .Deserialize(EngineSocketJson.Default.OwnerPromptPage)!;
        var item = Assert.Single(page.Items); Assert.Equal("client", item.ClientId); Assert.Equal(text, item.TextPreview);
        using var bulk = await Open(address, bulk: true);
        var chunk = Result(await Call(bulk, 21, "engine", "owner_prompt_chunk", Args(new OwnerPromptChunkArgs("selected", "client", item.ContentSha256, "text", 0,
            Machine: source.Machine, ExpectedSource: source), EngineSocketJson.Default.OwnerPromptChunkArgs))).Deserialize(EngineSocketJson.Default.OwnerPromptChunk)!;
        Assert.Equal(text, Encoding.UTF8.GetString(chunk.Data)); Assert.Equal(item.ContentSha256, chunk.ContentSha256);
        Error(await Call(control, 22, "engine", "owner_prompts", Args(new OwnerPromptReadArgs("selected", "contradictory-machine", ExpectedSource: source), EngineSocketJson.Default.OwnerPromptReadArgs)), "bad_args");
        var snapshots = await Message(control, source, "prompt/selected/client");
        Assert.Contains("prompt/selected/client", string.Join("", snapshots.Select(frame => frame.GetRawText())));
        await Send(control, "{\"t\":\"unsub\",\"id\":99}");
    }
    private static async Task<List<JsonElement>> Message(WebSocket socket, ExpectedSource source, string message)
    {
        await Send(socket, $$"""{"t":"sub","id":99,"stream":"engine","topic":"messages","key":{{JsonSerializer.Serialize(message, RowJsonContext.Rows.String)}},"machine":{{JsonSerializer.Serialize(source.Machine, RowJsonContext.Rows.String)}},"expectedSource":{{JsonSerializer.Serialize(source, RowJsonContext.Rows.ExpectedSource)}}}""");
        List<JsonElement> frames = [];
        do
        {
            var frame = await Read(socket); Assert.Equal("engine_snapshot", frame.GetProperty("t").GetString());
            Assert.Equal(source.Machine, frame.GetProperty("machine").GetString()); frames.Add(frame);
        } while (frames[^1].GetProperty("more").GetBoolean());
        return frames;
    }

    private static void Seed(TwoHosts.Host host, string id)
    { host.Sessions.Add(id, "codex", "source-derived", null!); host.Fixture.SeedSession(id, state: "running"); }
    private static long? Id(JsonElement frame) => frame.TryGetProperty("id", out var id) ? id.GetInt64() : null;
    private static JsonElement Element(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static JsonElement Args<T>(T args, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.SerializeToElement(args, type);
    private static JsonElement Result(JsonElement frame) { Assert.Equal("result", frame.GetProperty("t").GetString()); return frame.GetProperty("result"); }
    private static void Error(JsonElement frame, string code)
    { Assert.Equal("error", frame.GetProperty("t").GetString()); Assert.Equal(code, frame.GetProperty("code").GetString()); Assert.False(frame.TryGetProperty("result", out _)); }
    private static async Task<ClientWebSocket> Open(Uri address, bool bulk = false)
    {
        var socket = new ClientWebSocket(); using var stop = new CancellationTokenSource(Wait);
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket{(bulk ? "/bulk" : "")}"), stop.Token);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call(WebSocket socket, int id, string feature, string method, JsonElement args, ExpectedSource? frameSource = null)
    { await SendCall(socket, id, feature, method, args, frameSource); return await Read(socket); }
    private static Task SendCall(WebSocket socket, int id, string feature, string method, JsonElement args, ExpectedSource? source = null) => Send(socket,
        $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args.GetRawText()}}{{(source is null ? "" : ",\"expectedSource\":" + JsonSerializer.Serialize(source, RowJsonContext.Rows.ExpectedSource))}}}""");
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream(); using var stop = new CancellationTokenSource(Wait); var buffer = new byte[65536]; WebSocketReceiveResult received;
        do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token); Assert.Equal(WebSocketMessageType.Text, received.MessageType); bytes.Write(buffer, 0, received.Count); }
        while (!received.EndOfMessage);
        using var doc = JsonDocument.Parse(bytes.ToArray()); return doc.RootElement.Clone();
    }
}
