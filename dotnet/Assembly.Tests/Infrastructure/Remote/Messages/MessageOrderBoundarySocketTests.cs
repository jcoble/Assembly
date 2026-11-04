using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Infrastructure.Engine.Messages;
using Assembly.Tests.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class MessageReadSocketTests
{
    [Fact]
    public async Task Cancellation_of_a_collision_page_reaches_the_held_actual_owner_read()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "one", 100, sender: prefix + "a"); MessageReadFixture.Seed(hosts.Remote.Fixture, "two", 200, sender: prefix + "z");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db).Register(new MessageReadGate(calls, async (method, call) =>
        {
            if (method != "sender_order") return; entered.TrySetResult();
            try { await release.Task.WaitAsync(call.Cancellation); } catch (OperationCanceledException) when (call.Cancellation.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
        }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200, machine = hosts.Remote.Fixture.Machine }); await entered.Task.WaitAsync(Wait);
        await Send(socket, """{"t":"cancel","id":1}"""); await cancelled.Task.WaitAsync(Wait);
        Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task A_retired_single_oversized_recipient_needs_no_owner_fact_read()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var reads = 0; var f = hosts.Remote.Fixture;
        MessageReadFixture.Seed(f, "one", 100); MessageReadFixture.Seed(f, "two", 200);
        f.Db.Writer.Write("retired recorded authority", s => s.Execute("UPDATE engine_session SET state='ended',title=$title WHERE id='to'", new SqlArg("$title", new string('r', 70000))));
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, f.Db).Register(new MessageReadGate(calls, (method, _) =>
        { if (method == "receiver_order") { Interlocked.Increment(ref reads); throw new IOException("unavailable title authority"); } return Task.CompletedTask; }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "receiver", limit = 200, machine = f.Machine });
        Assert.Equal(new[] { "two", "one" }, Result(await Read(socket)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetProperty("id").GetString()));
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task Nullable_legacy_creation_membership_settles_registered_canonical_EOF_coverage()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var f = hosts.Remote.Fixture; var prefix = new string('n', 256);
        MessageReadFixture.Seed(f, "legacy", 100, sender: prefix + "a"); MessageReadFixture.Seed(f, "new", 200, sender: prefix + "z");
        f.Db.Writer.Write("source-derived pruned creation provenance", s => s.Execute("UPDATE engine_message SET message_created_seq=NULL WHERE id='legacy'"));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200, machine = f.Machine }); var page = Result(await Read(socket));
        Assert.Equal(new[] { "legacy", "new" }, page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetProperty("id").GetString()));
        Assert.True(!page.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null);
        Assert.Null(f.Text("SELECT message_created_seq FROM engine_message WHERE id='legacy'"));
    }

    [Fact]
    public async Task A_mixed_collision_compares_an_oversized_local_full_label_inside_SQL()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "local-z", 200, sender: prefix + new string('z', 70000));
        MessageReadFixture.Seed(hosts.Remote.Fixture, "remote-a", 100, sender: prefix + "a");
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200 });
        Assert.Equal(new[] { "remote-a", "local-z" }, Result(await Read(socket)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetProperty("id").GetString()));
    }
    [Fact]
    public async Task Queued_origin_only_message_orders_by_its_actual_foreign_recipient_title()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('r', 256);
        var recipient = hosts.Mac.Fixture; var origin = hosts.Remote.Fixture; recipient.SeedSession("to");
        recipient.Db.Writer.Write("actual local recipient title", s => s.Execute("UPDATE engine_session SET title=$title WHERE id='to'", new SqlArg("$title", prefix + "a")));
        MessageReadFixture.Seed(origin, "foreign-queued", 100, recipientMachine: recipient.Machine);
        MessageReadFixture.Seed(origin, "own-queued", 200, recipientMachine: recipient.Machine); origin.SeedSession("remote-target");
        origin.Db.Writer.Write("stale stub and actual second owner", s => s.Execute("""
            UPDATE engine_session SET title=$stale WHERE id='to';
            UPDATE engine_session SET title=$actual WHERE id='remote-target';
            UPDATE engine_message SET to_session_id='remote-target' WHERE id='own-queued';
            """, new SqlArg("$stale", prefix + "z stale"), new("$actual", prefix + "b")));
        Assert.Equal(0, recipient.Count("engine_message"));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "receiver", status = "queued", limit = 200, machine = origin.Machine });
        Assert.Equal(new[] { "foreign-queued", "own-queued" }, Result(await Read(socket)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetProperty("id").GetString()));
        Assert.Equal(0, recipient.Count("engine_message"));
    }

    [Fact]
    public async Task A_phone_peer_cannot_read_the_internal_full_name_facts()
    {
        await using var hosts = new TwoHosts();
        hosts.Mac.AdditionalFeatures = calls => new MessagesCalls(hosts.Mac.Runtime, hosts.Mac.Fixture.Db)
            .Register(new MessageReadGate(calls, (_, _) => Task.CompletedTask, new Assembly.Application.Common.Calls.PhonePeer("device")));
        MessageReadFixture.Seed(hosts.Mac.Fixture, "secret", 100); await hosts.Start(); using var socket = await Open(hosts.Mac, true);
        await Call(socket, 1, "sender_order", new { bucket = "", through = 0, at = (object?)null });
        Assert.Equal("forbidden", (await Read(socket)).GetProperty("code").GetString());
        await Call(socket, 2, "receiver_order", new { refs = new[] { new { sessionId = "to", version = 0 } } });
        Assert.Equal("forbidden", (await Read(socket)).GetProperty("code").GetString());
    }

}
