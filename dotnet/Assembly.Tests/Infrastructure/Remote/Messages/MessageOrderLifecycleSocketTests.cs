using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Engine.Messages;
using Assembly.Tests.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class MessageReadSocketTests
{
    [Fact]
    public async Task Twenty_thousand_complete_Jesse_rows_return_without_authority_reads()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var reads = 0;
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db)
            .Register(new MessageReadGate(calls, (method, _) =>
            {
                if (method is "sender_order" or "receiver_order") { Interlocked.Increment(ref reads); throw new IOException("authority unavailable"); }
                return Task.CompletedTask;
            }));
        for (var i = 0; i < 20000; i++) MessageReadFixture.Seed(hosts.Remote.Fixture, $"m{i:D5}", i, sender: "Jesse");
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200, machine = hosts.Remote.Fixture.Machine });
        var page = Result(await Read(socket)); Assert.Equal(200, page.GetProperty("items").GetArrayLength());
        Assert.Equal("m19999", page.GetProperty("items")[0].GetProperty("key").GetProperty("id").GetString());
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task Unrelated_session_during_collision_read_does_not_restart_but_old_page_still_invalidates()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "first", 100, sender: prefix + "a"); MessageReadFixture.Seed(hosts.Remote.Fixture, "second", 200, sender: prefix + "z");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db)
            .Register(new MessageReadGate(calls, async (method, call) =>
            { if (method == "sender_order") { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); } }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 1, machine = hosts.Remote.Fixture.Machine });
        await entered.Task.WaitAsync(Wait); hosts.Remote.Commit(1, "unrelated SESSION title"); await hosts.Reached(hosts.Remote.Through); release.TrySetResult();
        var page = Result(await Read(socket)); Assert.Equal("first", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("key").GetProperty("id").GetString());
        await Call(socket, 2, "page", new { sort = "sender", limit = 1, machine = hosts.Remote.Fixture.Machine, after = page.GetProperty("next").GetString() });
        Assert.Equal("query_changed", (await Read(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Referenced_receiver_hidden_tail_change_still_requires_the_exact_version()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var f = hosts.Remote.Fixture; var prefix = new string('r', 256);
        MessageReadFixture.Seed(f, "one", 100); MessageReadFixture.Seed(f, "two", 200); f.SeedSession("other");
        f.Db.Writer.Write("two actual incomplete titles", s => s.Execute("""
            UPDATE engine_session SET title=$a WHERE id='to';
            UPDATE engine_session SET title=$b WHERE id='other';
            UPDATE engine_message SET to_session_id='other' WHERE id='two';
            """, new SqlArg("$a", prefix + "a"), new("$b", prefix + "b")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, f.Db).Register(new MessageReadGate(calls, async (method, call) =>
        { if (method == "receiver_order") { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); } }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "receiver", limit = 200, machine = f.Machine }); await entered.Task.WaitAsync(Wait);
        f.Db.Writer.Write("hidden title tail/version", s =>
        {
            s.Execute("UPDATE engine_session SET title=$title,version=version+1 WHERE id='to'", new SqlArg("$title", prefix + "changed"));
            var version = s.QueryFirst("SELECT version FROM engine_session WHERE id='to'", r => r.Long(0));
            return EngineJournal.Commit(s, new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "fixture"), "to", version - 1, 0, 1001), version, 0,
                [new("session.changed", "session", "to", version, false, new("to", null, null, null, "idle", 0))], []);
        });
        release.TrySetResult(); Assert.Equal("query_changed", (await Read(socket)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Earlier_direct_rows_are_returned_before_a_later_collision_can_refuse()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var reads = 0; var prefix = new string('c', 256);
        for (var i = 0; i < 50; i++) MessageReadFixture.Seed(hosts.Mac.Fixture, $"a{i:D2}", i, sender: $"a{i:D2}");
        MessageReadFixture.Seed(hosts.Remote.Fixture, "blocked-a", 100, sender: prefix + new string('a', 40000));
        MessageReadFixture.Seed(hosts.Remote.Fixture, "blocked-z", 200, sender: prefix + new string('z', 40000));
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db).Register(new MessageReadGate(calls, (method, _) =>
        { if (method == "sender_order") Interlocked.Increment(ref reads); return Task.CompletedTask; }));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200 }); var page = Result(await Read(socket));
        Assert.Equal(50, page.GetProperty("items").GetArrayLength()); Assert.Equal(0, Volatile.Read(ref reads));
        await Call(socket, 2, "page", new { sort = "sender", limit = 200, after = page.GetProperty("next").GetString() });
        Assert.Equal("message_response_too_large", (await Read(socket)).GetProperty("code").GetString()); Assert.True(Volatile.Read(ref reads) > 0);
    }

    [Fact]
    public async Task Owner_EOF_without_canonical_scope_coverage_refuses_instead_of_losing_rows()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        MessageReadFixture.Seed(hosts.Remote.Fixture, "missing-a", 100, sender: prefix + "a"); MessageReadFixture.Seed(hosts.Remote.Fixture, "present-z", 200, sender: prefix + "z");
        await hosts.Start(); await hosts.StartMirror();
        // Actual source-local pruned row, with the old committed mirror deliberately retained.
        hosts.Remote.Fixture.Db.Writer.Write("pruned owning history", s => s.Execute("DELETE FROM engine_message WHERE id='missing-a'"));
        using var socket = await Open(hosts.Mac, false); await Call(socket, 1, "page", new { sort = "sender", limit = 200, machine = hosts.Remote.Fixture.Machine });
        Assert.Equal("query_changed", (await Read(socket)).GetProperty("code").GetString());
    }
}
