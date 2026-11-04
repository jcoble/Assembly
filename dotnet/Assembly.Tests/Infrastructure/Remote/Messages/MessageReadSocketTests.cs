using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Engine.Messages.Reading;
using Assembly.Infrastructure.Engine.Messages;
using Assembly.Tests.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class MessageReadSocketTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Registered_local_reads_preserve_original_body_and_reject_the_wrong_lane_and_closed_arguments()
    {
        await using var hosts = new TwoHosts();
        Register(hosts);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "local", 100, "  original\nbody%_\n");
        await hosts.Start();
        using var normal = await Open(hosts.Mac, false);
        await Call(normal, 1, "page", new { limit = 1 });
        var page = Result(await Read(normal));
        Assert.Equal("local", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("key").GetProperty("id").GetString());
        await Call(normal, 2, "count", new { session = new { machine = hosts.Mac.Fixture.Machine, id = "to" }, seenCuts = Array.Empty<object>() });
        Assert.Equal(1, Result(await Read(normal)).GetProperty("count").GetInt64());
        await Call(normal, 3, "body", new { messageId = "local", machine = hosts.Mac.Fixture.Machine });
        Assert.Equal("error", (await Read(normal)).GetProperty("t").GetString());
        await Call(normal, 4, "page", new { limit = 1, untrusted = true });
        Assert.Equal("bad_args", (await Read(normal)).GetProperty("code").GetString());
        using var bulk = await Open(hosts.Mac, true);
        await Call(bulk, 5, "body", new { messageId = "local", machine = hosts.Mac.Fixture.Machine });
        Assert.Equal("  original\nbody%_\n", Result(await Read(bulk)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Two_hosts_search_full_recorded_names_and_body_at_the_actual_origin_without_local_fallback()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        Register(hosts);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "same", 50, "wrong local body");
        var label = new string('x', 300) + "needle%_";
        MessageReadFixture.Seed(hosts.Remote.Fixture, "same", 100, "  remote original\n", sender: label);
        await hosts.Start(); await hosts.StartMirror();
        using var normal = await Open(hosts.Mac, false);
        await Call(normal, 1, "page", new { search = "needle%_", limit = 200 });
        var item = Assert.Single(Result(await Read(normal)).GetProperty("items").EnumerateArray());
        Assert.Equal(hosts.Remote.Fixture.Machine, item.GetProperty("key").GetProperty("originMachine").GetString());
        Assert.True(item.GetProperty("senderName").GetString()!.Length < label.Length);
        using var bulk = await Open(hosts.Mac, true);
        await Call(bulk, 2, "body", new { messageId = "same", machine = hosts.Remote.Fixture.Machine });
        var body = Result(await Read(bulk));
        Assert.Equal("  remote original\n", body.GetProperty("text").GetString());
        Assert.Equal(label, body.GetProperty("senderName").GetString());
        await hosts.DisconnectMirror();
        await Call(bulk, 3, "body", new { messageId = "same", machine = hosts.Remote.Fixture.Machine });
        Assert.Equal("error", (await Read(bulk)).GetProperty("t").GetString());
        await Call(normal, 4, "page", new { search = "needle%_", limit = 200 });
        var partial = Result(await Read(normal));
        Assert.Contains(partial.GetProperty("missingMachines").EnumerateArray(), value => value.GetString() == hosts.Remote.Fixture.Machine);
        Assert.Empty(partial.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Registered_bulk_match_treats_wildcards_literally_and_bounds_ids_before_reading()
    {
        await using var hosts = new TwoHosts(); Register(hosts);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "literal", 100, "%_");
        MessageReadFixture.Seed(hosts.Mac.Fixture, "ordinary", 101, "anything");
        await hosts.Start(); using var bulk = await Open(hosts.Mac, true);
        await Call(bulk, 1, "match", new { ids = new[] { "literal", "ordinary" }, search = "%_" });
        Assert.Equal("literal", Assert.Single(Result(await Read(bulk)).GetProperty("ids").EnumerateArray()).GetString());
        await Call(bulk, 2, "match", new { ids = Enumerable.Range(0, 201).Select(i => i.ToString()).ToArray(), search = "x" });
        Assert.Equal("bad_args", (await Read(bulk)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Relevant_mirror_changes_during_an_authority_match_refuse_the_old_cut_without_holding_the_writer()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "remote", 100, "needle");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db).Register(new MessageReadGate(calls, async (method, call) =>
        { if (method == "match") { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); } }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { search = "needle" }); await entered.Task.WaitAsync(Wait);
        hosts.Remote.Commit(1, "changed while search held"); await hosts.Reached(hosts.Remote.Through);
        release.TrySetResult();
        Assert.Equal("query_changed", (await Read(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancelling_a_page_cancels_its_actual_remote_authority_read()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "remote", 100, "needle");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db).Register(new MessageReadGate(calls, async (method, call) =>
        {
            if (method != "match") return;
            entered.TrySetResult();
            try { await release.Task.WaitAsync(call.Cancellation); }
            catch (OperationCanceledException) when (call.Cancellation.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
        }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { search = "needle" }); await entered.Task.WaitAsync(Wait);
        await Send(socket, """{"t":"cancel","id":1}""");
        await cancelled.Task.WaitAsync(Wait);
        Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Remote_snapshot_cut_survives_usage_only_advance_but_refuses_actual_session_changes()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "first", 100);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "second", 200);
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { limit = 1 }); var first = Result(await Read(socket));
        var next = first.GetProperty("next").GetString(); Assert.NotNull(next);
        hosts.Remote.Fixture.Db.Writer.Write("usage advance", scope => Assembly.Engine.Persistence.EngineJournal.Commit(scope,
            new(Guid.NewGuid(), hosts.Remote.Fixture.Machine, new(Assembly.Engine.Contracts.ActorKind.System, "fixture"), "usage", 0, 0, 1000), 1, 0,
            [new("usage.changed", "usage", "usage", 1, false, new(null, null, null, null, null, null))], []));
        await hosts.Reached(hosts.Remote.Through);
        await Call(socket, 4, "count", new { session = new { machine = hosts.Remote.Fixture.Machine, id = "to" }, seenCuts = first.GetProperty("cut") });
        var count = Result(await Read(socket)); Assert.Equal(0, count.GetProperty("count").GetInt64()); Assert.False(count.GetProperty("partial").GetBoolean());
        await Call(socket, 2, "page", new { limit = 1, after = next });
        Assert.Equal("first", Assert.Single(Result(await Read(socket)).GetProperty("items").EnumerateArray()).GetProperty("key").GetProperty("id").GetString());
        hosts.Remote.Commit(1, "actual renamed session"); await hosts.Reached(hosts.Remote.Through);
        await Call(socket, 3, "page", new { limit = 1, after = next });
        Assert.Equal("query_changed", (await Read(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_phone_peer_cannot_read_the_owner_traffic_or_internal_authority_match()
    {
        await using var hosts = new TwoHosts();
        hosts.Mac.AdditionalFeatures = calls => new MessagesCalls(hosts.Mac.Runtime, hosts.Mac.Fixture.Db).Register(
            new MessageReadGate(calls, (_, _) => Task.CompletedTask, new Assembly.Application.Common.Calls.PhonePeer("device")));
        MessageReadFixture.Seed(hosts.Mac.Fixture, "secret", 100, "private"); await hosts.Start();
        using var normal = await Open(hosts.Mac, false); await Call(normal, 1, "page", new { });
        Assert.Equal("forbidden", (await Read(normal)).GetProperty("code").GetString());
        using var bulk = await Open(hosts.Mac, true); await Call(bulk, 2, "match", new { ids = new[] { "secret" }, search = "private" });
        Assert.Equal("forbidden", (await Read(bulk)).GetProperty("code").GetString());
    }

    private static void Register(TwoHosts hosts)
    {
        hosts.Mac.AdditionalFeatures = calls => new MessagesCalls(hosts.Mac.Runtime, hosts.Mac.Fixture.Db, hosts.Mac.Forwarder).Register(calls);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db, hosts.Remote.Forwarder).Register(calls);
    }
    private static async Task<ClientWebSocket> Open(TwoHosts.Host host, bool bulk)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(host.Address) { Scheme = "ws", Path = bulk ? "/socket/bulk" : "/socket" }.Uri, default);
        await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task Call(ClientWebSocket socket, int id, string method, object args) => Send(socket, JsonSerializer.Serialize(new { t = "call", id, feature = "messages", method, args }));
    private static async Task Send(ClientWebSocket socket, string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);
    private static JsonElement Result(JsonElement frame) { Assert.Equal("result", frame.GetProperty("t").GetString()); return frame.GetProperty("result"); }
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(Wait); using var bytes = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); Assert.Equal(WebSocketMessageType.Text, frame.MessageType); bytes.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        Assert.True(bytes.Length <= 1024 * 1024); using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
}
