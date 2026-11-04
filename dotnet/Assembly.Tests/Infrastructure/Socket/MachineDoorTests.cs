using System.Security.Claims;
using Assembly.Engine.Mcp;
using Assembly.Engine.Mcp.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Application.Common;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-machine-" + Guid.NewGuid().ToString("N"));
    private readonly Assembly.Infrastructure.Topics.Topics _topics = new();
    private EngineFixture _f = null!;
    private EngineRuntime _runtime = null!;
    private MachineCalls _machineCalls = null!;
    private NeedAnswers _needs = null!;
    private SessionRegistry _needHosts = null!;
    private BackgroundWork _needWork = null!;
    private LiveHub _needHub = null!;
    private RelayPausedDatabase _relayDatabase = null!;
    private DeadlineTimeProvider _socketTime = null!;
    private readonly TaskCompletionSource _bulkClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionStore _store = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private readonly Launcher _launcher = new();
    private CallPeer? _peer;
    private bool _overridePeer;
    private TaskCompletionSource? _relayWriterPrepared, _relayWriterContinue, _relayWriterClosed;
    private TaskCompletionSource? _prepared;
    private TaskCompletionSource? _continue;


    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _f = new(topics: _topics); _store = SessionStore.Open(_f.Db, _f.Time, _root);
        _relayDatabase = new(_f.Db);
        _socketTime = new(_f.Time);
        _runtime = EngineRuntime.Start(_relayDatabase, _topics, _f.Time, _f.Limits, cutover: new(_f.Db, _store, _launcher, _root, _f.Time));
        var dispatcher = new SocketDispatcher(_f.Db.Writer, _topics, _f.Time, NullLogger.Instance, new EngineSocketStream(_runtime));
        _needHub = new(RowStore.Open(_f.Db)); _needWork = new(_f.Time, TextWriter.Null);
        _needHosts = new(_needHub, _store, _launcher, _f.Time, TextWriter.Null, _needWork);
        _needs = new(new(_runtime, _f.Limits, _f.Time), _runtime, _needHosts, _f.Time);
        _machineCalls = new MachineCalls(_runtime, _f.Time, _launcher, _root, _needs,
            new Assembly.Infrastructure.Engine.Handoff.NativeHandoffProducer(_launcher, _store));
        _machineCalls.Register(new PeerGate(dispatcher, this));
        dispatcher.Closed(_ => _bulkClosed.TrySetResult());
        new EngineCalls(_runtime, _f.Time, _needs).Register(dispatcher);
        dispatcher.Topic("peer", TestJson.Default.Note, n => n.Text, (_, c) => [new(c.Peer is OwnerPeer && c.ReplyId == 37 ? "owner" : "missing")]);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build(); _app.UseWebSockets();
        var endpoint = new SocketEndpoint("token", dispatcher, _runtime.Machine, _socketTime);
        var bulkEndpoint = new SocketEndpoint("token", dispatcher, _runtime.Machine, _socketTime, bulk: true);
        _app.Map("/socket/bulk", bulkEndpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(bulkEndpoint.CloseAll);
        _app.Map("/socket", endpoint.HandleAsync); _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new("ws://" + http.Authority + "/socket");
    }

    public async Task DisposeAsync()
    {
        await _needs.StopWaiting(); await _app.DisposeAsync(); await _machineCalls.DisposeAsync(); await _needs.DisposeAsync();
        await _runtime.DisposeAsync(); await _needHosts.DisposeAsync(); await _needWork.DisposeAsync(); _needHub.Dispose();
        _f.Dispose(); Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Authenticated_hello_propagates_owner_to_calls_and_subscriptions_and_refuses_other_roles()
    {
        using var socket = await Open();
        var location = await Location(socket, "parent");
        Assert.Equal(Path.Combine(_root, "parent"), location.GetProperty("result").GetProperty("folder").GetString());
        await Send(socket, """{"t":"sub","id":37,"topic":"peer"}""");
        Assert.Equal("owner", (await Read(socket)).GetProperty("items")[0].GetProperty("item").GetProperty("text").GetString());
        await Send(socket, """{"t":"unsub","id":37}""");
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("device") })
        {
            _overridePeer = true; _peer = peer;
            Assert.Equal("error", (await Location(socket, "parent")).GetProperty("t").GetString());
            Assert.Equal("error", (await Machine(socket, new RegisterForeignSession("source", "custom", "T"), Guid.NewGuid().ToString())).GetProperty("t").GetString());
        }
        Assert.Equal(0, _f.Count("engine_session")); Assert.Equal(0, _f.Count("engine_outbox")); Assert.Empty(_f.Events());
    }

    [Fact]
    public async Task Handoff_adopt_door_admits_only_the_transcript_it_can_verify_on_this_machine()
    {
        const string native = Assembly.Tests.Engine.Handoffs.NativeProvider.NativeId;
        var home = Path.Combine(_root, "home"); var folder = Path.Combine(_root, "target");
        _launcher.Native = new("claude", "Claude", "unused", [], new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = home }, VendorFamily: "claude");
        Directory.CreateDirectory(folder);
        var transcript = Path.Combine(home, "projects", Assembly.Infrastructure.Engine.Handoff.ClaudeSessionFile.ProjectKey(folder), native + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        await File.WriteAllTextAsync(transcript, Assembly.Tests.Engine.Handoffs.NativeProvider.Line("claude", folder, "remember kiwi") + "\n");
        var identity = new Assembly.Engine.Handoffs.HandoffIdentity("h", "source", "s", _runtime.Machine, 7, 8);
        var command = new AdoptSession("claude", "model", folder, "Adopted", native, "source", "s", 8);
        using var socket = await Open();
        async Task<Assembly.Engine.Handoffs.HandoffAck> Adopt(string capture)
        {
            var args = JsonSerializer.Serialize(new Assembly.Engine.Handoffs.HandoffAdoptArgs(identity, command, new string('a', 64), capture), EngineSocketJson.Default.HandoffAdoptArgs);
            await Send(socket, "{\"t\":\"call\",\"id\":7,\"feature\":\"engine\",\"method\":\"handoff_adopt\",\"args\":" + args + "}");
            var frame = await Read(socket); Assert.Equal("result", frame.GetProperty("t").GetString());
            return frame.GetProperty("result").Deserialize(EngineSocketJson.Default.HandoffAck)!;
        }
        var unverified = await Adopt(new string('b', 64));
        Assert.Equal(("native_witness_required", (CommandResult?)null), (unverified.Reason, unverified.Receipt));
        Assert.Equal(0, _f.Count("engine_session"));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(transcript)));
        var admitted = await Adopt(hash);
        Assert.True(admitted.Receipt?.Applied == true, admitted.Reason);
        Assert.Equal(native, _f.Text("SELECT provider_session_id FROM engine_session WHERE id='h'"));
        Assert.Equal(folder, _store.NativeLaunch("h")!.Folder);
        Assert.Equal(1, _f.Number("SELECT COUNT(*) FROM engine_outbox WHERE id='start/h/1' AND kind='start'"));
        Assert.Equal(admitted.Receipt, (await Adopt(hash)).Receipt);
    }

    [Fact]
    public async Task Wrong_token_and_local_source_do_not_create_visible_stub_or_effects()
    {
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(_address, default); await Send(socket, """{"t":"hello","protocol":1,"token":"wrong"}""");
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(4401, (int)socket.CloseStatus!);
        }
        using var owner = await Open();
        var refused = await Machine(owner, new RegisterForeignSession(_runtime.Machine, "p", "T"), Guid.NewGuid().ToString(), _runtime.Machine);
        Assert.Equal("error", refused.GetProperty("t").GetString());
        Assert.Equal(0, _f.Count("engine_session")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox"));
        foreach (var bad in new[] { "", ".", "..", "a/b", "../parent" })
            Assert.Equal("error", (await Location(owner, bad)).GetProperty("t").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Peer_start_projects_only_child_with_exact_repo_policy_and_replays_after_current_checks_change(bool managed)
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "source-custom", "Parent"), parent)).Applied);
        Assert.True(Result(await Ancestry(socket, new("source", parent, [new(parent, "source", "source-custom", "Parent", null)]))).Applied);
        Assert.Null(_store.Get(parent));
        var folder = managed ? (await Location(socket, parent)).GetProperty("result").GetProperty("folder").GetString()! : _root;
        var start = new StartSession("custom", null, folder, "Child", parent, null, null, managed ? "https://example.com/real.git" : null, managed);
        var commandId = Guid.NewGuid();
        var receipt = Result(await Machine(socket, start, child, commandId: commandId)); Assert.True(receipt.Applied, receipt.Error);
        var chat = _store.Get(child)!;
        Assert.Equal(parent, chat.ParentSessionId); Assert.True(chat.Helper); Assert.Equal("custom", chat.Agent);
        Assert.Equal(managed ? "https://example.com/real.git" : folder, chat.RepoUrl);
        Assert.Equal(folder, _store.Root(chat)); Assert.Null(chat.AcpSessionId); Assert.Null(_store.Get(parent));
        Assert.Equal(managed ? 1 : 0, _f.Number("SELECT managed_checkout FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Null(_f.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", child)));
        Assert.Equal(0, _launcher.ProviderCalls); Assert.Equal(1, _f.Count("engine_outbox"));
        _launcher.Available = false;
        Assert.Equal(receipt, Result(await Machine(socket, start, child, commandId: commandId)));
        Assert.Equal("command_id_conflict", Result(await Machine(socket, start with { Folder = folder + "-changed" }, child, commandId: commandId)).Error);
        Assert.Equal(1, _f.Number("SELECT COUNT(*) FROM session_info")); Assert.Single(_f.Events());
    }

    [Fact]
    public async Task Direct_replay_survives_folder_removal_but_new_intent_gets_immutable_refusal()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), parent)).Applied);
        Assert.True(Result(await Ancestry(socket, new("source", parent, [new(parent, "source", "p", "T", null)]))).Applied);
        var folder = Path.Combine(_root, "direct"); Directory.CreateDirectory(folder);
        var start = new StartSession("custom", null, folder, "Child", parent, null, null, ManagedCheckout: false);
        var id = Guid.NewGuid(); var accepted = Result(await Machine(socket, start, child, commandId: id)); Assert.True(accepted.Applied);
        Directory.Delete(folder);
        Assert.Equal(accepted, Result(await Machine(socket, start, child, commandId: id)));
        var fresh = Guid.NewGuid(); var freshChild = Guid.NewGuid().ToString();
        var refused = Result(await Machine(socket, start, freshChild, commandId: fresh)); Assert.Equal("bad_args", refused.Error);
        Directory.CreateDirectory(folder);
        Assert.Equal(refused, Result(await Machine(socket, start, freshChild, commandId: fresh)));
        Assert.Equal(2, _f.Count("engine_session")); Assert.Equal(1, _f.Number("SELECT COUNT(*) FROM session_info")); Assert.Single(_f.Events());
    }

    [Fact]
    public async Task Unsupported_machine_commands_forged_actor_and_bad_metadata_never_acquire_authority()
    {
        using var socket = await Open();
        foreach (var command in new EngineCommand[] { new StopSession(), new Assembly.Engine.NeedsMe.ResolveNeed("dismissed", null),
            new Assembly.Engine.Runs.StartRun("owner", "https://example.com/task", null, null, 1000, []), new Assembly.Engine.Handoffs.HandoffOutcome("resumed", 1, null) })
            Assert.Equal("forbidden", Result(await Machine(socket, command, Guid.NewGuid().ToString())).Error);
        await Send(socket, """{"t":"call","id":8,"feature":"engine","method":"machine_command","args":{"commandId":"00000000-0000-0000-0000-000000000001","fromMachine":"source","entityId":"00000000-0000-0000-0000-000000000002","expectedVersion":0,"epoch":0,"actor":{"kind":"system","id":"machine:source"},"command":{"kind":"register_foreign_session","machine":"source","provider":"p","title":"T"}}}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
        Assert.Equal("bad_args", Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), "not-a-uuid")).Error);
        Assert.Equal(0, _f.Count("engine_session")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Registration_receipt_failure_rolls_back_its_invisible_stub_and_publications()
    {
        using var socket = await Open(); var publications = 0;
        using var engineSubscription = _topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        using var chatSubscription = _topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        _f.Db.Writer.Write("inject", s => s.Execute("CREATE TRIGGER fail BEFORE INSERT ON engine_command_receipt BEGIN SELECT RAISE(ABORT,'injected'); END"));
        Assert.Equal("error", (await Machine(socket, new RegisterForeignSession("source", "p", "T"), Guid.NewGuid().ToString())).GetProperty("t").GetString());
        Assert.Equal(0, _f.Count("engine_session")); Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Equal(0, publications);
    }

    [Fact]
    public async Task Parent_ownership_is_rechecked_inside_writer_after_preparation()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), parent)).Applied);
        _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously); _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Machine(socket, new StartSession("custom", null, Path.Combine(_root, parent), "Child", parent, null, null, "actual-source", true), child);
        await _prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _f.Db.Writer.Write("replace-source", scope => scope.Execute("UPDATE engine_session SET machine='other',holder='other' WHERE id=$id", new SqlArg("$id", parent)));
        _continue.TrySetResult();
        Assert.Equal("forbidden", Result(await pending).Error);
        Assert.Equal(1, _f.Count("engine_session")); Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Same_writer_projector_rejects_contradictory_managed_location_without_partial_state()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), parent)).Applied);
        Assert.True(Result(await Ancestry(socket, new("source", parent, [new(parent, "source", "p", "T", null)]))).Applied);
        var before = _f.Count("engine_command_receipt");
        Assert.Throws<CallFailed>(() => _f.Db.Writer.Write("trusted-invalid-start", scope => _runtime.Commands.Apply(scope,
            new(Guid.NewGuid(), _runtime.Machine, new(ActorKind.System, "machine:source"), child, 0, 0, 1000),
            new StartSession("custom", null, _root, "Child", parent, null, null, "actual-source", true))));
        Assert.Equal(before, _f.Count("engine_command_receipt")); Assert.Equal(1, _f.Count("engine_session"));
        Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Actual_list_sessions_after_reopen_preserves_nullable_peer_child_model_and_excludes_foreign_parent()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString(); var sibling = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "Foreign"), parent)).Applied);
        Assert.True(Result(await Ancestry(socket, new("source", parent, [new(parent, "source", "p", "Foreign", null)]))).Applied);
        var start = new StartSession("custom", null, _root, "Child", parent, null, null, ManagedCheckout: false);
        Assert.True(Result(await Machine(socket, start, child)).Applied);
        Assert.True(Result(await Machine(socket, start with { Title = "Sibling" }, sibling)).Applied);
        await _runtime.DisposeAsync(); _f.Reopen();
        _store = SessionStore.Open(_f.Db, _f.Time, _root);
        _runtime = EngineRuntime.Start(_f.Db, _topics, _f.Time, _f.Limits, cutover: new(_f.Db, _store, _launcher, _root, _f.Time));
        var gate = new McpGate(_runtime, _f.Limits, _f.Time);
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await using var server = McpServer.Create(new StreamServerTransport(input, output), new());
        var request = new RequestContext<CallToolRequestParams>(server,
            new() { Method = "tools/call", Id = new RequestId(1) }, new() { Name = "list_sessions" })
        { User = new ClaimsPrincipal(new ClaimsIdentity([new("assembly:session", child), new("assembly:epoch", "0")], "test")) };
        var answer = await new ListSessionsTool(gate).InvokeAsync(request);
        Assert.False(answer.IsError);
        using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(answer.Content)).Text);
        var row = Assert.Single(json.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(sibling, row.GetProperty("sessionId").GetString()); Assert.Equal("sibling", row.GetProperty("relation").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("model").ValueKind); Assert.Equal("custom", row.GetProperty("provider").GetString());
        Assert.Null(_store.Get(parent));
    }

    [Fact]
    public async Task Invalid_managed_envelopes_and_parent_ownership_refuse_without_partial_child()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), parent)).Applied);
        var location = Path.Combine(_root, parent);
        var valid = new StartSession("custom", null, location, "T", parent, null, null, "actual-source", true);
        foreach (var start in new[] { valid with { RepoUrl = null }, valid with { Folder = _root }, valid with { ManagedCheckout = false }, valid with { Provider = "unknown" }, valid with { Model = "" }, valid with { ParentId = Guid.NewGuid().ToString() } })
            Assert.False(Result(await Machine(socket, start, Guid.NewGuid().ToString())).Applied);
        Assert.False(Result(await Machine(socket, valid, Guid.NewGuid().ToString(), "other")).Applied);
        Assert.Equal(1, _f.Count("engine_session")); Assert.Equal(0, _f.Count("engine_outbox")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info"));
    }

    [Theory]
    [InlineData("engine_session")]
    [InlineData("session_info")]
    [InlineData("engine_command_receipt")]
    public async Task Insert_projection_and_receipt_failures_roll_back_the_same_transaction(string table)
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "p", "T"), parent)).Applied);
        Assert.True(Result(await Ancestry(socket, new("source", parent, [new(parent, "source", "p", "T", null)]))).Applied);
        _f.Db.Writer.Write("inject", s => s.Execute($"CREATE TRIGGER fail BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'injected'); END"));
        var before = _f.Count("engine_command_receipt"); var publications = 0;
        using var engineSubscription = _topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        using var chatSubscription = _topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        var result = await Machine(socket, new StartSession("custom", null, _root, "T", parent, null, null), child);
        Assert.Equal("error", result.GetProperty("t").GetString());
        Assert.Equal(1, _f.Count("engine_session")); Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info"));
        Assert.Equal(before, _f.Count("engine_command_receipt")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox")); Assert.Equal(0, publications);
    }

    private async Task<ClientWebSocket> Open()
    {
        var socket = new ClientWebSocket(); await socket.ConnectAsync(_address, default);
        await Send(socket, """{"t":"hello","protocol":1,"token":"token"}"""); Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static CommandResult Result(JsonElement frame) { Assert.Equal("result", frame.GetProperty("t").GetString()); return frame.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!; }
    private async Task<JsonElement> Location(WebSocket socket, string parent)
    {
        var args = JsonSerializer.Serialize(new StartLocationArgs(parent), EngineSocketJson.Default.StartLocationArgs);
        await Send(socket, "{\"t\":\"call\",\"id\":5,\"feature\":\"engine\",\"method\":\"start_location\",\"args\":" + args + "}"); return await Read(socket);
    }
    private static async Task<JsonElement> Machine(WebSocket socket, EngineCommand command, string entity, string source = "source", Guid? commandId = null)
    {
        var args = JsonSerializer.Serialize(new MachineCommand(commandId ?? Guid.NewGuid(), source, entity, 0, 0, command), EngineSocketJson.Default.MachineCommand);
        await Send(socket, "{\"t\":\"call\",\"id\":6,\"feature\":\"engine\",\"method\":\"machine_command\",\"args\":" + args + "}"); return await Read(socket);
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var bytes = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, timeout.Token); Assert.Equal(WebSocketMessageType.Text, frame.MessageType); bytes.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
    private sealed class Launcher : IAgentLauncher
    {
        public bool Available = true; public int ProviderCalls; public AgentDescriptor? Native;
        public IReadOnlyList<AgentDescriptor> Agents => !Available ? [] : Native is { } native ? [new("custom", "Target", "unused", [], null), native] : [new("custom", "Target", "unused", [], null)];
        public void ValidateFolder(string folder) => new AdapterLauncher([], "", new UnclaimedPermissions()).ValidateFolder(folder);
        public bool CanRunTerminalAuthentication => false;
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
            StageTimes stages, CancellationToken cancellationToken) => LaunchAsync(descriptor.Id, stages, cancellationToken);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> effectiveEnvironment,
            Assembly.Application.Common.Acp.IPermissionPolicy policy, StageTimes stages, CancellationToken cancellationToken) =>
            LaunchAsync(descriptor.Id, stages, cancellationToken);

        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) { ProviderCalls++; throw new InvalidOperationException("No provider at start"); }
    }
    private sealed class PeerGate(ICallRegistry inner, MachineDoorTests fixture) : ICallRegistry
    {
        private CallContext Peer(CallContext call) => fixture._overridePeer ? call with { Peer = fixture._peer } : call;
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<R>> handler, bool bulk = false) => inner.Read(feature, method, args, result, (a, c) => handler(a, Peer(c)), bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler, Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, async (a, c) =>
        {
            var write = await handler(a, Peer(c));
            if (a is MachineCommand { Command: StartSession } && fixture._prepared is { } prepared)
            {
                prepared.TrySetResult(); await fixture._continue!.Task.WaitAsync(c.Cancellation);
            }
            if (a is Assembly.Engine.Relays.RelaySendCommitArgs && fixture._relayWriterPrepared is { } relayPrepared)
            {
                using var closed = c.Closed.Register(() => fixture._relayWriterClosed!.TrySetResult());
                relayPrepared.TrySetResult(); await fixture._relayWriterContinue!.Task;
            }
            return write;
        }, order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
}
