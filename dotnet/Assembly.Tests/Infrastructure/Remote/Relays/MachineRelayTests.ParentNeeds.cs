using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Tests.Infrastructure.Remote.Relays;

public sealed partial class MachineRelayTests
{
    [Theory]
    [InlineData(false, 16, false)]
    [InlineData(true, 16, false)]
    [InlineData(false, 4194321, true)]
    [InlineData(true, 4194321, true)]
    public async Task Actual_parent_need_coordinator_preserves_note_answer_and_one_dispatch_both_directions(bool remoteParent, int size, bool loseReply)
    {
        var delivery = new Delivery();
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery, macEffects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await h.Start();
        var source = remoteParent ? h.Remote : h.Mac; var target = remoteParent ? h.Mac : h.Remote;
        var (parent, child, need) = SeedParentNeed(source, target, size);
        await h.StartMirror();
        var lost = 0;
        var dropping = remoteParent ? source : target;
        dropping.WriteCommitted = what =>
        {
            if (loseReply && what == (remoteParent ? "engine/parent_need_outcome" : "engine/need_answer_execute") &&
                Interlocked.CompareExchange(ref lost, 1, 0) == 0)
            {
                if (remoteParent) dropping.Endpoint.DropAll(); else dropping.BulkEndpoint.DropAll();
            }
        };
        var answer = JsonSerializer.SerializeToElement(new { action = "accept", content = new { body = new string('a', size) + "\0λ😀", nil = (string?)null } });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = source.Runtime.Publisher.Register(item =>
        {
            if (item.Kind == "parent_need.requested") requested.TrySetResult();
        }, out _);
        var waiting = source.Needs.ApplyAsync(new McpCaller(parent, 3), new(need, ParentNeedAction.Answer, answer), default, timeout.Token);
        await requested.Task.WaitAsync(timeout.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal("open", target.Fixture.State("engine_needs_me", need));
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        relay.Start();
        CommandResult result;
        try { result = await waiting.WaitAsync(timeout.Token); }
        catch (OperationCanceledException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
        Assert.True(result.Applied, result.Error);
        Assert.Equal("answered", target.Fixture.State("engine_needs_me", need));
        using var actual = JsonDocument.Parse(target.Fixture.Text("SELECT answer_json FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need))!);
        Assert.True(JsonElement.DeepEquals(answer, actual.RootElement));
        Assert.Equal(1, source.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND state='done' AND attempt=1"));
        Assert.Equal(1, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'parent-need/v1/%'"));
        Assert.Equal(loseReply ? 1 : 0, lost);
        var note = "need/" + need;
        Assert.Equal("system", source.Fixture.Text("SELECT sender_kind FROM engine_message WHERE id=$id", new SqlArg("$id", note)));
        var original = target.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id=$id", row => row.Blob(0), default, new SqlArg("$id", note));
        var routed = source.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id=$id", row => row.Blob(0), default, new SqlArg("$id", note));
        Assert.True(original!.AsSpan().SequenceEqual(routed));
        if (size > 4194304)
        {
            var full = target.Fixture.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_needs_me WHERE id=$id", row => row.Blob(0), default, new SqlArg("$id", need))!;
            Assert.True(full.AsSpan().SequenceEqual(RelayHash.Utf8.GetBytes(new string('q', size) + "\0FULL_NOTEλ😀")));
            var display = target.Runtime.Reader.Entity("needs_me", need, default)!.Value;
            Assert.False(display.GetProperty("textComplete").GetBoolean());
            Assert.Equal(full.LongLength, display.GetProperty("textUtf8Bytes").GetInt64());
            Assert.True(RelayHash.Utf8.GetByteCount(display.GetRawText()) < 32 * 1024);
        }
        Assert.Equal(parent, target.Fixture.Text("SELECT answered_by FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
    }

    [Fact]
    public async Task Actual_remote_parent_and_remote_child_use_the_mac_coordinator_without_reverse_connections()
    {
        var delivery = new Delivery();
        await using var target = new TwoHosts.Host(effects: delivery, machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await using var h = new TwoHosts(forwardCommands: true, effects: delivery, macEffects: delivery,
            machineLauncher: new RelayTestLauncher(), checkouts: Path.GetTempPath());
        await target.Start(); await h.Start();
        var (parent, child, need) = SeedParentNeed(h.Remote, target, 16);
        await h.StartMirror(); await h.AddMirror("workbox-test-two", target);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiting = h.Remote.Needs.ApplyAsync(new(parent, 3), new(need, ParentNeedAction.Escalate, null), default, timeout.Token);
        await using var relay = new MachineRelay(h.Mac.Runtime, h.Mac.Forwarder!, h.Connections, h.Mac.Topics, h.Mac.Fixture.Time, h.MirrorLog, h.Mac.Needs);
        relay.Start();
        CommandResult result;
        try { result = await waiting.WaitAsync(timeout.Token); }
        catch (OperationCanceledException error) { throw new InvalidOperationException(h.MirrorLog.ToString(), error); }
        Assert.True(result.Applied, result.Error);
        Assert.Equal("open", target.Fixture.State("engine_needs_me", need));
        Assert.Null(target.Fixture.Text("SELECT addressee_session_id FROM engine_needs_me WHERE id=$id", new SqlArg("$id", need)));
        Assert.Equal(1, h.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need' AND state='done' AND attempt=1"));
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='parent_need'"));
        Assert.Equal(1, target.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'parent-need/v1/%'"));
    }

    private static (string Parent, string Child, string Need) SeedParentNeed(TwoHosts.Host source, TwoHosts.Host target, int size)
    {
        var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString(); var need = Guid.NewGuid().ToString();
        source.Fixture.SeedSession(parent, epoch: 3);
        Assert.True(target.Fixture.Apply(new RegisterForeignSession(source.Runtime.Machine, "codex", "Parent"), parent,
            actor: new(ActorKind.System, "machine:" + source.Runtime.Machine)).Applied);
        target.Fixture.SeedSession(child, parentId: parent, epoch: 7);
        var ancestry = source.Runtime.Commands.PrepareForeignAncestry(new(target.Runtime.Machine, child,
            [new(parent, source.Runtime.Machine, "codex", "Test", null), new(child, target.Runtime.Machine, "codex", "Test", parent)]));
        Assert.True(source.Fixture.Db.Writer.Write("test.foreign-child", scope => source.Runtime.Commands.ApplyForeignAncestryBatch(scope, ancestry)).Applied);
        var command = EngineCommands.PrepareNeedCommand(new OpenNeed(child, null, null, "question", new string('q', size) + "\0FULL_NOTEλ😀", [], null, null, null));
        var opened = target.Fixture.Db.Writer.Write("test.need", scope => target.Runtime.Commands.ApplyPreparedNeed(scope,
            new(Guid.NewGuid(), target.Runtime.Machine, new(ActorKind.System, "adapter"), need, 0, 7, 1000), command));
        Assert.True(opened.Applied, opened.Error);
        return (parent, child, need);
    }
}
