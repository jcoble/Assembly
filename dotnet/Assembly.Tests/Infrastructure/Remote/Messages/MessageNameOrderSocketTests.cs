using Assembly.Infrastructure.Engine.Messages;
using Assembly.Tests.Engine.Messages;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class MessageReadSocketTests
{
    [Fact]
    public async Task Direct_collision_direct_continuations_keep_the_actual_anchor_lower_bound()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts);
        var prefix = new string('c', 256);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "a1", 1, sender: "aa");
        MessageReadFixture.Seed(hosts.Mac.Fixture, "c1", 2, sender: prefix + "a");
        MessageReadFixture.Seed(hosts.Remote.Fixture, "c2", 3, sender: prefix + "b");
        hosts.Remote.Fixture.Db.Writer.Write("legacy unframed label", s => s.Execute("UPDATE engine_message SET framed=0 WHERE id='c2'"));
        MessageReadFixture.Seed(hosts.Mac.Fixture, "m1", 4, sender: "mm");
        MessageReadFixture.Seed(hosts.Mac.Fixture, "n1", 5, sender: "nn");
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        string? after = null; List<string> ids = []; HashSet<string> cursors = []; var call = 0;
        do
        {
            await Call(socket, ++call, "page", new { sort = "sender", limit = 1, after }); var page = Result(await Read(socket));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetProperty("id").GetString()!));
            after = page.TryGetProperty("next", out var next) ? next.GetString() : null;
            if (after is not null) Assert.True(cursors.Add(after));
        } while (after is not null);
        Assert.Equal(new[] { "a1", "c1", "c2", "m1", "n1" }, ids);
    }

    [Fact]
    public async Task Mixed_known_and_absent_sender_proof_stays_a_blocked_bucket()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        MessageReadFixture.Seed(hosts.Mac.Fixture, "known-z", 200, sender: prefix + "z");
        MessageReadFixture.Seed(hosts.Remote.Fixture, "unknown-a", 100, sender: prefix + "a");
        hosts.Remote.Fixture.Db.Writer.Write("unframed incomplete proof", s => s.Execute("UPDATE engine_message SET framed=0 WHERE id='unknown-a'"));
        await hosts.Start(); await hosts.StartMirror();
        // Source-derived unchanged preboolean cache: preserve all other real source facts.
        hosts.Mac.Fixture.Db.Writer.Write("legacy missing order certificate", write => write.Execute(
            "UPDATE engine_mirror_row SET row_json=json_remove(row_json,'$.senderNameComplete') WHERE machine=$machine AND entity_kind='message' AND entity_id='unknown-a'",
            new Assembly.Application.Common.Database.SqlArg("$machine", hosts.Remote.Fixture.Machine)));
        Assert.Equal(0, hosts.Mac.Fixture.Number("SELECT count(*) FROM engine_mirror_row WHERE entity_id='unknown-a' AND json_type(row_json,'$.senderNameComplete') IS NOT NULL"));
        using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200 }); var page = Result(await Read(socket));
        Assert.Equal(new[] { "unknown-a", "known-z" }, page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("key").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Remote_801_shared_prefix_full_tail_cannot_hide_behind_the_first_candidate_page()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var prefix = new string('n', 256);
        for (var i = 0; i < 801; i++) MessageReadFixture.Seed(hosts.Remote.Fixture, $"m{i:D4}", i, sender: prefix + (i == 0 ? "a" : "z"));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "sender", limit = 200, machine = hosts.Remote.Fixture.Machine });
        var items = Result(await Read(socket)).GetProperty("items").EnumerateArray().ToArray();
        Assert.InRange(items.Length, 2, 200); Assert.Equal("m0000", items[0].GetProperty("key").GetProperty("id").GetString());
        Assert.Equal("m0800", items[1].GetProperty("key").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("receiver")]
    public async Task One_oversized_actual_name_authority_never_reads_full_facts(string sort)
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var reads = 0;
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db)
            .Register(new MessageReadGate(calls, (method, _) =>
            {
                if (method is "sender_order" or "receiver_order") { Interlocked.Increment(ref reads); throw new IOException("unexpected authority read"); }
                return Task.CompletedTask;
            }));
        MessageReadFixture.Seed(hosts.Remote.Fixture, "only", 100, sender: new string('s', 70000));
        hosts.Remote.Fixture.Db.Writer.Write("oversized title", s => s.Execute("UPDATE engine_session SET title=$title WHERE id='to'",
            new Assembly.Application.Common.Database.SqlArg("$title", new string('r', 70000))));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort, limit = 200, machine = hosts.Remote.Fixture.Machine });
        Assert.Equal("only", Assert.Single(Result(await Read(socket)).GetProperty("items").EnumerateArray()).GetProperty("key").GetProperty("id").GetString());
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task Two_busy_complete_recipient_titles_use_direct_SQL_without_owner_reads()
    {
        await using var hosts = new TwoHosts(forwardCommands: true); Register(hosts); var reads = 0;
        hosts.Remote.AdditionalFeatures = calls => new MessagesCalls(hosts.Remote.Runtime, hosts.Remote.Fixture.Db)
            .Register(new MessageReadGate(calls, (method, _) =>
            {
                if (method is "sender_order" or "receiver_order") { Interlocked.Increment(ref reads); throw new IOException("unexpected title read"); }
                return Task.CompletedTask;
            }));
        var f = hosts.Remote.Fixture; MessageReadFixture.Seed(f, "one", 100); MessageReadFixture.Seed(f, "two", 200); f.SeedSession("other");
        f.Db.Writer.Write("actual busy whole titles", s => s.Execute("""
            UPDATE engine_session SET title='Same short title',state='running',version=version+1 WHERE id IN ('to','other');
            UPDATE engine_message SET to_session_id='other' WHERE id='two';
            """));
        await hosts.Start(); await hosts.StartMirror(); using var socket = await Open(hosts.Mac, false);
        await Call(socket, 1, "page", new { sort = "receiver", limit = 200, machine = f.Machine });
        Assert.Equal(new[] { "two", "one" }, Result(await Read(socket)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetProperty("id").GetString()));
        Assert.Equal(0, Volatile.Read(ref reads));
    }
}
